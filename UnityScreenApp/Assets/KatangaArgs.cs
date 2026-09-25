using System;
using System.Collections.Generic;
using System.IO;

// Katanga's options: the command line, plus katanga_options.txt next to katanga.exe.
//
// 3DFixManager starts Katanga with its own fixed arguments, so options such as
// --cpu-isolation, --game-priority, --no-dither or --no-frame-sync can go in that file
// instead, whitespace separated, with # starting a comment.  Command line options come first.

public static class KatangaArgs
{
    public const string OptionsFile = "katanga_options.txt";

    static string[] all;

    public static string[] All
    {
        get
        {
            if (all == null)
            {
                var list = new List<string>(Environment.GetCommandLineArgs());
                try
                {
                    string exeFolder = Path.GetDirectoryName(list[0]);
                    string file = Path.Combine(exeFolder ?? "", OptionsFile);
                    if (File.Exists(file))
                    {
                        foreach (string line in File.ReadAllLines(file))
                        {
                            string text = line;
                            int comment = text.IndexOf('#');
                            if (comment >= 0)
                                text = text.Substring(0, comment);
                            list.AddRange(text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
                        }
                    }
                }
                catch (Exception)
                {
                    // An unreadable options file must never stop Katanga, the command line stands.
                }
                all = list.ToArray();
            }
            return all;
        }
    }

    public static bool Has(string option)
    {
        return Array.IndexOf(All, option) >= 0;
    }
}
