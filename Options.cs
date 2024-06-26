using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ResxScanner
{
    public class Options
    {
        [Option('s', "source", Required = true, HelpText = "The solution path.")]
        public string Source { get; set; }

        [Option('d', "destination", Required = false, HelpText = "The file path of the output json file. default is 'key.json' relative to the solution directory")]
        public string Destination { get; set; } = "locale-keys.json";

        [Option('m', "max-path-count", Required = false, HelpText = "The file path of the output json file. default is 'key.json' relative to the solution directory")]
        public int MaxPathCount { get; set; } = 20;
    }
}