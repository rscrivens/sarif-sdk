﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.CodeAnalysis.Sarif.Driver;
using Microsoft.CodeAnalysis.Sarif.Writers;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.Sarif
{
    public class SarifLoggerTests : JsonTests
    {
        private readonly ITestOutputHelper output;

        public SarifLoggerTests(ITestOutputHelper output)
        {
            this.output = output;
        }      

        [Fact]
        public void SarifLogger_RedactedCommandLine()
        {
            var sb = new StringBuilder();

            // On a developer's machine, the script BuildAndTest.cmd runs the tests with a particular command line. 
            // Under AppVeyor, the appveyor.yml file simply specifies the names of the test assemblies, and AppVeyor 
            // constructs and executes its own, different command line. So, based on our knowledge of each of those 
            // command lines, we select a different token to redact in each of those cases.
            //
            //
            // Sample test execution command-line from within VS. We will redact the 'TestExecution' role data
            //
            // "C:\PROGRAM FILES (X86)\MICROSOFT VISUAL STUDIO 14.0\COMMON7\IDE\COMMONEXTENSIONS\MICROSOFT\TESTWINDOW\te.processhost.managed.exe"
            // /role=TestExecution /wexcommunication_connectionid=2B1B7D58-C573-45E8-8968-ED321963F0F6
            // /stackframecount=50 /wexcommunication_protocol=ncalrpc
            //
            // Sample test execution from command-line when running test script. Will redact hostProcessId
            //
            // "C:\Program Files (x86\\Microsoft Visual Studio 14.0\Common7\IDE\QTAgent32_40.exe\" 
            // /agentKey a144e450-ac06-46d0-8365-c21ea7872d23 /hostProcessId 8024 /hostIpcPortName 
            // eqt -60284c64-6bc1-3ecc-fb5f-a484bb1a2475"
            // 
            // Sample test execution from Appveyor will redact 'Appveyor'
            //
            // pathToExe   = C:\Program Files (x86)\Microsoft Visual Studio 14.0\Common7\IDE\CommonExtensions\Microsoft\TestWindow\Extensions
            // commandLine = vstest.console  /logger:Appveyor "C:\projects\sarif-sdk\bld\bin\Sarif.UnitTests\AnyCPU_Release\Sarif.UnitTests.dll"

            using (var textWriter = new StringWriter(sb))
            {
                string[] tokensToRedact = new string[] { };
                string pathToExe = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location);
                string commandLine = Environment.CommandLine;
                string lowerCaseCommandLine = commandLine.ToLower();

                if (lowerCaseCommandLine.Contains("testhost.dll") || lowerCaseCommandLine.Contains("\\xunit.console") || lowerCaseCommandLine.Contains("testhost.x86.exe"))
                {
                    int index = commandLine.LastIndexOf("\\");
                    string argumentToRedact = commandLine.Substring(0, index + 1);
                    tokensToRedact = new string[] { argumentToRedact };
                }
                else if (pathToExe.IndexOf(@"\Extensions", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    string appVeyor = "Appveyor";
                    if (commandLine.IndexOf(appVeyor, StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        // For Appveyor builds, redact the string Appveyor.
                        tokensToRedact = new string[] { appVeyor };
                    }
                    else
                    {
                        // The calling assembly lives in an \Extensions directory that hangs off
                        // the directory of the test driver (the location of which we can't retrieve
                        // from Assembly.GetEntryAssembly() as we are running in an AppDomain).
                        pathToExe = pathToExe.Substring(0, pathToExe.Length - @"\Extensions".Length);
                        tokensToRedact = new string[] {  pathToExe };
                    }
                }
                else if (commandLine.Contains("/agentKey"))
                {
                    string argumentToRedact = commandLine.Split(new string[] { @"/agentKey" }, StringSplitOptions.None)[1].Trim();
                    argumentToRedact = argumentToRedact.Split(' ')[0];
                    tokensToRedact = new string[] { argumentToRedact };
                }
                else
                {
                    Assert.False(true, pathToExe + " " + commandLine);
                }

                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: null,
                    loggingOptions: LoggingOptions.None,
                    invocationTokensToRedact: tokensToRedact,
                    invocationPropertiesToLog: new List<string> { "CommandLine" })) { }

                string result = sb.ToString();
                result.Split(new string[] { SarifConstants.RedactedMarker }, StringSplitOptions.None)
                    .Length.Should().Be(tokensToRedact.Length + 1, "redacting n tokens gives you n+1 removal markers");
            }
        }

        [Theory]
        // These values are emitted both verbose and non-verbose
        [InlineData(FailureLevel.Error, true, true)]
        [InlineData(FailureLevel.Error, false, true)]
        [InlineData(FailureLevel.Warning, true, true)]
        [InlineData(FailureLevel.Warning, false, true)]

        // These values are emitted only in verbose mode
        [InlineData(FailureLevel.Note, true, true)]
        [InlineData(FailureLevel.Note, false, false)]
        [InlineData(FailureLevel.None, true, true)]
        [InlineData(FailureLevel.None, false, false)]
        public void SarifLogger_ShouldLogByFailureLevel(FailureLevel level, bool verboseLogging, bool expectedReturn)
        {
            LoggingOptions loggingOptions = verboseLogging ? LoggingOptions.Verbose : LoggingOptions.None;

            var sb = new StringBuilder();
            var logger = new SarifLogger(new StringWriter(sb), loggingOptions);
            bool result = logger.ShouldLog(level);
            result.Should().Be(expectedReturn);
        }

        [Fact]
        public void SarifLogger_ShouldLogRecognizesAllFailureLevels()
        {
            LoggingOptions loggingOptions = LoggingOptions.Verbose;
            var sb = new StringBuilder();
            var logger = new SarifLogger(new StringWriter(sb), loggingOptions);

            foreach (object resultLevelObject in Enum.GetValues(typeof(FailureLevel)))
            {
                // The point of this test is that every defined enum value
                // should pass a call to ShouldLog and will not raise an 
                // exception because the enum value isn't recognized
                logger.ShouldLog((FailureLevel)resultLevelObject);
            }
        }

        [Fact]
        public void SarifLogger_WritesSarifLoggerVersion()
        {
            var sb = new StringBuilder();

            using (var textWriter = new StringWriter(sb))
            {
                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: new string[] { @"example.cpp" },
                    loggingOptions: LoggingOptions.None,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: null))
                {
                    LogSimpleResult(sarifLogger);
                }
            }

            string output = sb.ToString();
            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(output);

            string sarifLoggerLocation = typeof(SarifLogger).Assembly.Location;
            string expectedVersion = FileVersionInfo.GetVersionInfo(sarifLoggerLocation).FileVersion;
        }

        [Fact]
        public void SarifLogger_WritesRunProperties()
        {
            string propertyName = "numberValue";
            double propertyValue = 3.14;
            string logicalId = nameof(logicalId) + ":" + Guid.NewGuid().ToString();
            string baselineInstanceGuid = nameof(baselineInstanceGuid) + ":" + Guid.NewGuid().ToString();
            string runInstanceGuid = Guid.NewGuid().ToString();
            string automationLogicalId = nameof(automationLogicalId) + ":" + Guid.NewGuid().ToString();
            string runInstanceId = automationLogicalId + "/" + runInstanceGuid;
            string architecture = nameof(architecture) + ":" + "x86";
            var conversion = new Conversion() { Tool = DefaultTool };
            var utcNow = DateTime.UtcNow;
            var versionControlUri = new Uri("https://www.github.com/contoso/contoso");
            var versionControlDetails = new VersionControlDetails() { RepositoryUri = versionControlUri, AsOfTimeUtc = DateTime.UtcNow };
            string originalUriBaseIdKey = "testBase";
            Uri originalUriBaseIdValue = new Uri("https://sourceserver.contoso.com");
            var originalUriBaseIds = new Dictionary<string, ArtifactLocation>() { { originalUriBaseIdKey, new ArtifactLocation { Uri = originalUriBaseIdValue } } };
            string defaultEncoding = "UTF7";
            List<string> redactionTokens = new List<string> { "[MY_REDACTION_TOKEN]" };


            var sb = new StringBuilder();

            var run = new Run();

            using (var textWriter = new StringWriter(sb))
            {
                run.SetProperty(propertyName, propertyValue);

                run.AutomationDetails = new RunAutomationDetails
                {
                    Id = runInstanceId,
                    Guid = runInstanceGuid,
                };

                run.BaselineGuid = baselineInstanceGuid;
                run.Conversion = conversion;
                run.VersionControlProvenance = new[] { versionControlDetails };
                run.OriginalUriBaseIds = originalUriBaseIds;
                run.DefaultEncoding = defaultEncoding;
                run.RedactionTokens = redactionTokens;

                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    run: run,
                    invocationPropertiesToLog: null))
                {
                }
            }

            string output = sb.ToString();
            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(output);

            run = sarifLog.Runs[0];

            run.GetProperty<double>(propertyName).Should().Be(propertyValue);
            run.AutomationDetails.Guid.Should().Be(runInstanceGuid);
            run.BaselineGuid.Should().Be(baselineInstanceGuid);
            run.AutomationDetails.Id.Should().Be(runInstanceId);
            run.Conversion.Tool.Should().BeEquivalentTo(DefaultTool);
            run.VersionControlProvenance[0].RepositoryUri.Should().BeEquivalentTo(versionControlUri);
            run.OriginalUriBaseIds[originalUriBaseIdKey].Uri.Should().Be(originalUriBaseIdValue);
            run.DefaultEncoding.Should().Be(defaultEncoding);
            run.RedactionTokens[0].Should().Be(redactionTokens[0]);
        }

        [Fact]
        public void SarifLogger_WritesFileData()
        {
            var sb = new StringBuilder();
            string file;

            using (var tempFile = new TempFile(".cpp"))
            using (var textWriter = new StringWriter(sb))
            {
                file = tempFile.Name;
                File.WriteAllText(file, "#include \"windows.h\";");

                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: new string[] { file },
                    dataToInsert: OptionallyEmittedData.Hashes,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: null))
                {
                    LogSimpleResult(sarifLogger);
                }
            }

            string logText = sb.ToString();

            string fileDataKey = new Uri(file).AbsoluteUri;

            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(logText);
            sarifLog.Runs[0].Artifacts[0].MimeType.Should().Be(MimeType.Cpp);
            sarifLog.Runs[0].Artifacts[0].Hashes.Keys.Count.Should().Be(3);
            sarifLog.Runs[0].Artifacts[0].Hashes["md5"].Should().Be("4B9DC12934390387862CC4AB5E4A2159");
            sarifLog.Runs[0].Artifacts[0].Hashes["sha-1"].Should().Be("9B59B1C1E3F5F7013B10F6C6B7436293685BAACE");
            sarifLog.Runs[0].Artifacts[0].Hashes["sha-256"].Should().Be("0953D7B3ADA7FED683680D2107EE517A9DBEC2D0AF7594A91F058D104B7A2AEB");
        }

        [Fact]
        public void SarifLogger_WritesFileDataWithUnrecognizedEncoding()
        {
            var sb = new StringBuilder();
            string file;
            string fileText = "using System;";

            using (var tempFile = new TempFile(".cs"))
            using (var textWriter = new StringWriter(sb))
            {
                file = tempFile.Name;
                File.WriteAllText(file, fileText);

                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: new string[] { file },
                    dataToInsert: OptionallyEmittedData.TextFiles,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: null,
                    defaultFileEncoding: "ImaginaryEncoding"))
                {
                    LogSimpleResult(sarifLogger);
                }
            }

            string logText = sb.ToString();

            string fileDataKey = new Uri(file).AbsoluteUri;
            byte[] fileBytes = Encoding.Default.GetBytes(fileText);

            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(logText);
            Artifact fileData = sarifLog.Runs[0].Artifacts[0];
            fileData.MimeType.Should().Be(MimeType.CSharp);
            fileData.Contents.Binary.Should().Be(Convert.ToBase64String(fileBytes));
            fileData.Contents.Text.Should().BeNull();
        }

        [Fact]
        public void SarifLogger_ScrapesFilesFromResult()
        {
            var sb = new StringBuilder();

            using (var textWriter = new StringWriter(sb))
            {
                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: null,
                    dataToInsert: OptionallyEmittedData.Hashes,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: null))
                {
                    string ruleId = "RuleId";
                    var rule = new ReportingDescriptor { Id = ruleId };

                    var result = new Result
                    {
                        RuleId = ruleId,
                        Message = new Message { Text = "Some testing occurred." },
                        AnalysisTarget = new ArtifactLocation { Uri = new Uri(@"file:///file0.cpp") },
                        Locations = new[]
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation
                                {
                                    ArtifactLocation = new ArtifactLocation
                                    {
                                        Uri = new Uri(@"file:///file1.cpp")
                                    }
                                }
                            },
                        },
                        Fixes = new[]
                        {
                            new Fix
                            {
                                ArtifactChanges = new[]
                                {
                                   new ArtifactChange
                                   {
                                    ArtifactLocation = new ArtifactLocation
                                    {
                                        Uri = new Uri(@"file:///file2.cpp")
                                    },
                                    Replacements = new[]
                                    {
                                        new Replacement {
                                            DeletedRegion = new Region { StartLine = 1}
                                        }
                                    }
                                   }
                                },
                            }
                        },
                        RelatedLocations = new[]
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation
                                {
                                    ArtifactLocation = new ArtifactLocation
                                    {
                                        Uri = new Uri(@"file:///file3.cpp")
                                    }
                                }
                            }
                        },
                        Stacks = new[]
                        {
                            new Stack
                            {
                                Frames = new[]
                                {
                                    new StackFrame
                                    {
                                        Location = new Location
                                        {
                                            PhysicalLocation = new PhysicalLocation
                                            {
                                                ArtifactLocation = new ArtifactLocation
                                                {
                                                    Uri = new Uri(@"file:///file4.cpp")
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        },
                        CodeFlows = new[]
                        {
                            new CodeFlow
                            {
                                ThreadFlows = new[]
                                {
                                    new ThreadFlow
                                    {
                                        Locations = new[]
                                        {
                                            new ThreadFlowLocation
                                            {
                                                Location = new Location
                                                {
                                                    PhysicalLocation = new PhysicalLocation
                                                    {
                                                        ArtifactLocation = new ArtifactLocation
                                                        {
                                                            Uri = new Uri(@"file:///file5.cpp")
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    };

                    sarifLogger.Log(rule, result);
                }
            }

            string logText = sb.ToString();
            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(logText);

            int fileCount = 6;

            for (int i = 0; i < fileCount; ++i)
            {
                string fileName = @"file" + i + ".cpp";
                string fileDataKey = "file:///" + fileName;
                sarifLog.Runs[0].Artifacts.Where(f => f.Location.Uri.AbsoluteUri.ToString().Contains(fileDataKey)).Any().Should().BeTrue();
            }

            sarifLog.Runs[0].Artifacts.Count.Should().Be(fileCount);
        }

        [Fact]
        public void SarifLogger_DoNotScrapeFilesFromNotifications()
        {
            var sb = new StringBuilder();

            using (var textWriter = new StringWriter(sb))
            {
                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: null,
                    dataToInsert: OptionallyEmittedData.Hashes,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: null))
                {                    
                    var toolNotification = new Notification
                    {
                        Locations = new List<Location>
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation { ArtifactLocation = new ArtifactLocation { Uri = new Uri(@"file:///file.cpp") } }
                            }
                        },
                        Message = new Message { Text = "A notification was raised." }
                    };
                    sarifLogger.LogToolNotification(toolNotification);

                    var configurationNotification = new Notification
                    {
                        Locations = new List<Location>
                        {
                            new Location
                            {
                                PhysicalLocation = new PhysicalLocation { ArtifactLocation = new ArtifactLocation { Uri = new Uri(@"file:///file.cpp") } }
                            }
                        },
                        Message = new Message { Text = "A notification was raised." }
                    };
                    sarifLogger.LogConfigurationNotification(configurationNotification);

                    string ruleId = "RuleId";
                    var rule = new ReportingDescriptor { Id = ruleId };

                    var result = new Result
                    {
                        RuleId = ruleId,
                        Message = new Message { Text = "Some testing occurred." }
                    };

                    sarifLogger.Log(rule, result);
                }
            }

            string logText = sb.ToString();
            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(logText);

            sarifLog.Runs[0].Artifacts.Should().BeNull();
        }

        [Fact]
        public void SarifLogger_LogsStartAndEndTimesByDefault()
        {
            var sb = new StringBuilder();

            using (var textWriter = new StringWriter(sb))
            {
                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: null,
                    dataToInsert: OptionallyEmittedData.Hashes,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: null))
                {
                    LogSimpleResult(sarifLogger);
                }
            }

            string logText = sb.ToString();
            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(logText);

            Invocation invocation = sarifLog.Runs[0].Invocations?[0];
            invocation.StartTimeUtc.Should().NotBe(DateTime.MinValue);
            invocation.EndTimeUtc.Should().NotBe(DateTime.MinValue);

            // Other properties should be empty.
            invocation.CommandLine.Should().BeNull();
            invocation.WorkingDirectory.Should().BeNull();
            invocation.ProcessId.Should().Be(0);
            invocation.ExecutableLocation.Should().BeNull();
        }

        [Fact]
        public void SarifLogger_LogsSpecifiedInvocationProperties()
        {
            var sb = new StringBuilder();

            using (var textWriter = new StringWriter(sb))
            {
                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: null,
                    dataToInsert: OptionallyEmittedData.Hashes,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: new[] { "WorkingDirectory", "ProcessId" }))
                {
                    LogSimpleResult(sarifLogger);
                }
            }

            string logText = sb.ToString();
            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(logText);

            Invocation invocation = sarifLog.Runs[0].Invocations[0];

            // StartTime and EndTime should still be logged.
            invocation.StartTimeUtc.Should().NotBe(DateTime.MinValue);
            invocation.EndTimeUtc.Should().NotBe(DateTime.MinValue);

            // Specified properties should be logged.
            invocation.WorkingDirectory.Should().NotBeNull();
            invocation.ProcessId.Should().NotBe(0);

            // Other properties should be empty.
            invocation.CommandLine.Should().BeNull();
            invocation.ExecutableLocation.Should().BeNull();
        }

        [Fact]
        public void SarifLogger_TreatsInvocationPropertiesCaseInsensitively()
        {
            var sb = new StringBuilder();

            using (var textWriter = new StringWriter(sb))
            {
                using (var sarifLogger = new SarifLogger(
                    textWriter,
                    analysisTargets: null,
                    dataToInsert: OptionallyEmittedData.Hashes,
                    invocationTokensToRedact: null,
                    invocationPropertiesToLog: new[] { "WORKINGDIRECTORY", "prOCessID" }))
                {
                    LogSimpleResult(sarifLogger);
                }
            }

            string logText = sb.ToString();
            var sarifLog = JsonConvert.DeserializeObject<SarifLog>(logText);

            Invocation invocation = sarifLog.Runs[0].Invocations?[0];

            // Specified properties should be logged.
            invocation.WorkingDirectory.Should().NotBeNull();
            invocation.ProcessId.Should().NotBe(0);
        }

        private void LogSimpleResult(SarifLogger sarifLogger)
        {
            ReportingDescriptor rule = new ReportingDescriptor { Id = "RuleId" };
            sarifLogger.Log(rule, CreateSimpleResult(rule));
        }

        private Result CreateSimpleResult(ReportingDescriptor rule)
        {           
            return new Result
            {
                RuleId = rule.Id,
                Message = new Message { Text = "Some testing occurred." }
            };
        }

        [Fact]
        public void SarifLogger_ResultAndRuleIdMismatch()
        {
            var sb = new StringBuilder();

            using (var writer = new StringWriter(sb))
            using (var sarifLogger = new SarifLogger(writer, LoggingOptions.Verbose))
            {
                var rule = new ReportingDescriptor
                {
                    Id = "ActualId"
                };

                var result = new Result
                {
                    RuleId = "IncorrectRuleId",
                    Message = new Message { Text = "test message" }
                };

                Assert.Throws<ArgumentException>(() => sarifLogger.Log(rule, result));
            }
        }        

        [Fact]
        public void SarifLogger_LoggingOptions()
        {
            foreach (object loggingOptionsObject in Enum.GetValues(typeof(LoggingOptions)))
            {
                TestForLoggingOption((LoggingOptions)loggingOptionsObject);
            }
        }


        // This helper is intended to validate a single enum member only
        // and not arbitrary combinations of bits. One defined member,
        // All, contains all bits.
        private void TestForLoggingOption(LoggingOptions loggingOption)
        {
            string fileName = Path.GetTempFileName();

            try
            {
                SarifLogger logger;

                // Validates overload that accept a path argument.
                using (logger = new SarifLogger(fileName, loggingOption))
                {
                    ValidateLoggerForExclusiveOption(logger, loggingOption);
                };

                // Validates overload that accepts any 
                // TextWriter (for example, one instantiated over a
                // StringBuilder instance).
                var sb = new StringBuilder();
                var stringWriter = new StringWriter(sb);
                using (logger = new SarifLogger(stringWriter, loggingOption))
                {
                    ValidateLoggerForExclusiveOption(logger, loggingOption);
                };
            }            
            finally
            {
                if (File.Exists(fileName)) { File.Delete(fileName); }
            }
        }

        private void ValidateLoggerForExclusiveOption(SarifLogger logger, LoggingOptions loggingOptions)
        {
            switch (loggingOptions)
            {
                case LoggingOptions.None:
                {
                    logger.OverwriteExistingOutputFile.Should().BeFalse();
                    logger.PrettyPrint.Should().BeFalse();
                    logger.Verbose.Should().BeFalse();
                    break;
                }
                case LoggingOptions.OverwriteExistingOutputFile:
                {
                    logger.OverwriteExistingOutputFile.Should().BeTrue();
                    logger.PrettyPrint.Should().BeFalse();
                    logger.Verbose.Should().BeFalse();
                    break;
                }
                case LoggingOptions.PrettyPrint:
                {
                    logger.OverwriteExistingOutputFile.Should().BeFalse();
                    logger.PrettyPrint.Should().BeTrue();
                    logger.Verbose.Should().BeFalse();
                    break;
                }
                case LoggingOptions.Verbose:
                {
                    logger.OverwriteExistingOutputFile.Should().BeFalse();
                    logger.PrettyPrint.Should().BeFalse();
                    logger.Verbose.Should().BeTrue();
                    break;
                }
                case LoggingOptions.All:
                {
                    logger.OverwriteExistingOutputFile.Should().BeTrue();
                    logger.PrettyPrint.Should().BeTrue();
                    logger.Verbose.Should().BeTrue();
                    break;
                }
                default:
                {
                    throw new ArgumentException();
                }
            }
        }
    }
}
