using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// The options window: hold Shift while starting katanga.exe (standalone or from 3DFixManager).
//
// Older Unity players had their own Shift/Alt "Screen Selector" dialog at launch; Unity 6 has
// none.  This shows Katanga's options with an explanation of each, before Unity starts XR or the
// game, and writes the choices to katanga_options.txt next to katanga.exe (see KatangaArgs), so
// they apply to every start, including 3DFixManager's with its fixed arguments.
//
// Checkboxes read as "this feature is on", so the --no-... options are shown inverted.  Options
// in the file that this list doesn't know are kept as they are.

public static class KatangaOptions
{
    class Option
    {
        public string Section;       // heading shown above this option, or null
        public string Flag;
        public bool Inverted;        // checkbox on = flag absent
        public string Title;
        public string Help;
        public string DefaultValue;  // numeric value after the flag, or null for none
        public string ValueLabel;    // shown before the value field
        public bool WholeNumber;     // the value must be a whole number of at least 1
        public string OffValue;      // for options that are on by default: unchecked writes the
                                     // flag with this value instead of leaving it out
    }

    static readonly Option[] options =
    {
        new Option {
            Section = "Frame pacing", Flag = "--no-frame-sync", Inverted = true,
            Title = "Frame sync: pace the game to the headset",
            Help = "The game shows exactly one new frame per headset refresh, so turning and panning stay free of "
                 + "judder. Keep 3DFixManager's VR frame limiter at unlimited while this is on: a second limiter "
                 + "fights the pacing." },
        new Option {
            Flag = "--no-gpu-wait", Inverted = true,
            Title = "Wait until the game has finished each frame on the GPU",
            Help = "Katanga only takes a game frame once the graphics card has finished drawing it. Without this "
                 + "the headset sometimes gets the previous frame again, which stutters depending on where you look "
                 + "(Metro Exodus). The wait uses part of the game's frame time: if a game still misses frames, "
                 + "lower its graphics settings or the headset refresh rate rather than turning this off." },

        new Option {
            Section = "Picture", Flag = "--no-color-correction", Inverted = true,
            Title = "Colour correction for Virtual Desktop",
            Help = "Virtual Desktop lifts shadows and mid-tones of VR apps and adds blue, which flattens contrast "
                 + "and colour. This undoes it, so the headset shows the colours a native Quest app would. "
                 + "Measured on a Quest 3; only used with Virtual Desktop, other runtimes are left untouched." },
        new Option {
            Flag = "--no-white-fix", Inverted = true,
            Title = "Neutral white",
            Help = "On the Quest 3 blue runs out before white, so full white looks yellowish next to the greys. This "
                 + "keeps white at the same tint as the greys, about 9% less bright at the very top. Only with the "
                 + "colour correction." },
        new Option {
            Flag = "--screen-sharpen", DefaultValue = "0.5", OffValue = "0", ValueLabel = "strength:",
            Title = "Screen sharpening",
            Help = "Sharpens the game image at the size the screen is shown at, so it looks crisp without the "
                 + "crunchy, shimmering edges of sharpening the whole view (PRISM, one of the sharpening modes on "
                 + "the controller; leave that on RCAS or off). 0.5 is a good start; from about 0.6 the smallest "
                 + "text starts to break up." },
        new Option {
            Flag = "--no-dither", Inverted = true,
            Title = "Dithering",
            Help = "Adds fine noise, invisible at headset frame rates, where the image goes to 8 bits per colour. "
                 + "Removes banding in dark gradients such as fog and shadows. Turn off only if you see grain." },

        new Option {
            Section = "NVIDIA driver", Flag = "--no-vsync-profile", Inverted = true,
            Title = "Keep vertical sync off for katanga.exe",
            Help = "3DFixManager turns vertical sync on for every program, which holds Katanga to your monitor's "
                 + "refresh rate (60 fps on a 60 Hz TV) instead of the headset's. Katanga gives katanga.exe an "
                 + "NVIDIA profile with vertical sync off. Takes effect from the next start." },

        new Option {
            Section = "CPU (experimental, no measured benefit so far)", Flag = "--cpu-isolation", DefaultValue = "1",
            Title = "Separate CPU cores for Katanga", ValueLabel = "cores:", WholeNumber = true,
            Help = "Keeps Katanga, including Virtual Desktop's busy frame timing thread, on the last physical cores "
                 + "(the number) and the game on all the others. May help games that load every core." },
        new Option {
            Flag = "--isolate-spinner",
            Title = "Own CPU for Virtual Desktop's timing thread",
            Help = "Virtual Desktop keeps one thread in Katanga spinning at a full core, for exact frame timing. This "
                 + "gives only that thread a logical CPU of its own; everything else keeps all the others." },
        new Option {
            Flag = "--game-priority",
            Title = "Game above normal priority",
            Help = "Runs the game above normal priority, so background programs make way for it." },
    };

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    [DllImport("UnityNativePlugin64", CharSet = CharSet.Unicode)]
    static extern int ShowOptionsDialog(string title, string intro, string spec, StringBuilder result, int resultLength);

    const int VK_SHIFT = 0x10;

    // As early as possible: before XR starts, and before anything reads the options.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ShowIfShiftHeld()
    {
        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0)
            return;

        try
        {
            Show();
        }
        catch (Exception e)
        {
            Debug.Log("Options window failed: " + e.Message);
        }
    }

    static void Show()
    {
        string path = KatangaArgs.OptionsPath;
        List<string> tokens = ReadTokens(path);

        // Current state from the file, and whatever else is in it.
        var used = new bool[tokens.Count];
        var spec = new StringBuilder();
        foreach (Option o in options)
        {
            int at = tokens.IndexOf(o.Flag);
            string value = o.DefaultValue;
            if (at >= 0)
            {
                used[at] = true;
                if (o.DefaultValue != null && at + 1 < tokens.Count && ParseNumber(tokens[at + 1], out _))
                {
                    value = tokens[at + 1];
                    used[at + 1] = true;
                }
            }
            bool on = (at >= 0) != o.Inverted;
            if (o.OffValue != null)
            {
                // On unless the file sets the off value; then show the default for turning it back on.
                on = !(at >= 0 && SameNumber(value, o.OffValue));
                if (!on)
                    value = o.DefaultValue;
            }
            if (o.Section != null)
                spec.Append("#\t").Append(o.Section).Append('\n');
            spec.Append("o\t").Append(o.Title + (o.ValueLabel != null ? ", " + o.ValueLabel : "")).Append('\t').Append(o.Help).Append('\t')
                .Append(on ? "1" : "0").Append('\t').Append(value ?? "").Append('\n');
        }
        var others = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
            if (!used[i])
                others.Add(tokens[i]);

        string intro = "These settings are saved in " + KatangaArgs.OptionsFile + " next to katanga.exe and used every time "
                     + "Katanga starts, also from 3DFixManager. Hold Shift while starting Katanga to come back here."
                     + (others.Count > 0 ? "\n\nAlso in the file, kept as they are: " + String.Join(" ", others) : "");

        var result = new StringBuilder(4096);
        int saved = ShowOptionsDialog("Katanga options", intro, spec.ToString(), result, result.Capacity);
        if (saved != 1)
        {
            Debug.Log("Options window: closed without saving");
            return;
        }

        string[] lines = result.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != options.Length)
            return;

        var file = new StringBuilder();
        file.AppendLine("# Katanga options, read in addition to the command line (3DFixManager passes its own).");
        file.AppendLine("# Written by the options window: hold Shift while starting katanga.exe. It can also be");
        file.AppendLine("# edited by hand: whitespace separated, # starts a comment.");
        for (int i = 0; i < options.Length; i++)
        {
            Option o = options[i];
            string[] f = lines[i].Split('\t');
            bool on = f[0] == "1";
            string value = o.DefaultValue;
            if (f.Length > 1 && ParseNumber(f[1], out float number) && number >= 0 &&
                (!o.WholeNumber || (number >= 1 && number == Mathf.Floor(number))))
                value = number.ToString(System.Globalization.CultureInfo.InvariantCulture);

            file.AppendLine();
            foreach (string help in Wrap(o.Title + ". " + o.Help, 95))
                file.AppendLine("# " + help);
            if (o.OffValue != null)
            {
                // On by default: active only when it differs from the default.
                if (!on)
                    file.AppendLine(o.Flag + " " + o.OffValue);
                else
                    file.AppendLine((SameNumber(value, o.DefaultValue) ? "#" : "") + o.Flag + " " + value);
                continue;
            }
            string line = o.Flag + (o.DefaultValue != null ? " " + value : "");
            bool active = on != o.Inverted;
            file.AppendLine(active ? line : "#" + line);
        }
        if (others.Count > 0)
        {
            file.AppendLine();
            file.AppendLine("# Other options");
            file.AppendLine(String.Join(" ", others));
        }

        File.WriteAllText(path, file.ToString());
        KatangaArgs.Reload();
        Debug.Log("Options window: saved " + path);
    }

    // Values like 0.5; a decimal comma is accepted too.
    static bool ParseNumber(string text, out float value)
    {
        return float.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    static bool SameNumber(string a, string b)
    {
        return ParseNumber(a, out float x) && ParseNumber(b, out float y) && Mathf.Approximately(x, y);
    }

    static List<string> ReadTokens(string path)
    {
        var tokens = new List<string>();
        if (!File.Exists(path))
            return tokens;
        foreach (string raw in File.ReadAllLines(path))
        {
            string text = raw;
            int comment = text.IndexOf('#');
            if (comment >= 0)
                text = text.Substring(0, comment);
            tokens.AddRange(text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }
        return tokens;
    }

    static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
            yield return line.ToString();
    }
}
