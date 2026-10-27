using System.IO;
namespace MemEco.App.Services;

internal enum CliMode
{
    /// <summary>引数なし / --autostart / --minimized: トレイ常駐。</summary>
    Resident,

    /// <summary>--show: パネルを表示して常駐。</summary>
    Show,

    /// <summary>--selftest &lt;path&gt;: CoreSelfTest を実行して終了。</summary>
    SelfTest,

    /// <summary>--preview &lt;path&gt;: パネルを PNG に書き出して終了。</summary>
    Preview,

    /// <summary>--reclaim &lt;path&gt;: 今すぐ解放を 1 回だけ実行して終了。</summary>
    Reclaim,

    /// <summary>--preview-settings &lt;path&gt;: 設定画面を PNG に書き出して終了（開発用）。</summary>
    SettingsPreview,
}

/// <summary>コマンドライン引数の解釈（DESIGN §7）。</summary>
internal sealed class CliArgs
{
    private CliArgs()
    {
    }

    public CliMode Mode { get; private init; } = CliMode.Resident;

    /// <summary>出力先の絶対パス（--selftest / --preview / --reclaim）。</summary>
    public string? OutputPath { get; private init; }

    /// <summary>--autostart / --minimized が指定されたか。</summary>
    public bool StartHidden { get; private init; }

    public static CliArgs Parse(string[] args)
    {
        CliMode mode = CliMode.Resident;
        string? output = null;
        bool hidden = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].Trim();
            switch (arg.ToLowerInvariant())
            {
                case "--show":
                    mode = CliMode.Show;
                    break;
                case "--autostart":
                case "--minimized":
                    hidden = true;
                    break;
                case "--selftest":
                    mode = CliMode.SelfTest;
                    output = TakePath(args, ref i) ?? DefaultOutput("selftest.txt");
                    break;
                case "--preview":
                    mode = CliMode.Preview;
                    output = TakePath(args, ref i) ?? DefaultOutput("preview-main.png");
                    break;
                case "--reclaim":
                    mode = CliMode.Reclaim;
                    output = TakePath(args, ref i) ?? DefaultOutput("reclaim.json");
                    break;
                case "--preview-settings":
                    mode = CliMode.SettingsPreview;
                    output = TakePath(args, ref i) ?? DefaultOutput("preview-settings.png");
                    break;
            }
        }

        return new CliArgs
        {
            Mode = mode,
            OutputPath = output,
            StartHidden = hidden,
        };
    }

    private static string? TakePath(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        string candidate = args[index + 1].Trim();
        if (candidate.Length == 0 || candidate.StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        index++;
        return ResolvePath(candidate);
    }

    private static string DefaultOutput(string fileName)
        => Path.Combine(Environment.CurrentDirectory, "artifacts", fileName);

    public static string ResolvePath(string path)
    {
        string full = Path.IsPathRooted(path)
            ? path
            : Path.Combine(Environment.CurrentDirectory, path);
        return Path.GetFullPath(full);
    }
}
