﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.using System;

using System.Collections.Generic;
using CommandLine;

namespace Microsoft.CodeAnalysis.Sarif.Driver
{
    internal class MultipleFilesOptionsBase : CommonOptionsBase
    {
        [Option(
            'r',
            "recurse",
            Default = false,
            HelpText = "Recursively select subdirectories in paths.")]
        public bool Recurse { get; internal set; }

        [Option(
            'o',
            "output-folder",
            HelpText = "A folder to output the transformed files to.")]
        public string OutputFolderPath { get; internal set; }


        [Option(
            'i',
            "inline",
            Default = false,
            HelpText = "Write all newly generated content to the input file.")]
        public bool Inline { get; set; }


        [Value(0,
            MetaName = "<files>",
            HelpText = "Files to process (wildcards ? and * allowed).",
            Required = true)]
        public IEnumerable<string> TargetFileSpecifiers { get; internal set; }
    }
}
