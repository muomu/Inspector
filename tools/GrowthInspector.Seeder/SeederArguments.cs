namespace GrowthInspector.Seeder;

/// <summary>命令行参数解析：只认 --db / --seed / --help，其余一律报错。</summary>
internal sealed record SeederArguments(string DatabasePath, string SeedDirectory, bool ShowHelp)
{
    public const string DefaultSeedDirectory = "seed/growth";

    public static SeederArguments Parse(IReadOnlyList<string> args)
    {
        string? databasePath = null;
        string? seedDirectory = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    return new SeederArguments(string.Empty, DefaultSeedDirectory, true);

                case "--db":
                    databasePath = TakeValue(args, ref i, "--db");
                    break;

                case "--seed":
                    seedDirectory = TakeValue(args, ref i, "--seed");
                    break;

                default:
                    if (TrySplit(arg, "--db", out var dbValue))
                    {
                        databasePath = dbValue;
                    }
                    else if (TrySplit(arg, "--seed", out var seedValue))
                    {
                        seedDirectory = seedValue;
                    }
                    else
                    {
                        throw new SeederUsageException($"未知参数：{arg}");
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new SeederUsageException("必须指定 --db <sqlite 路径>");
        }

        return new SeederArguments(
            Path.GetFullPath(databasePath),
            Path.GetFullPath(string.IsNullOrWhiteSpace(seedDirectory) ? DefaultSeedDirectory : seedDirectory),
            false);
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int index, string name)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new SeederUsageException($"{name} 缺少取值");
        }

        index++;
        var value = args[index];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SeederUsageException($"{name} 缺少取值");
        }

        return value;
    }

    private static bool TrySplit(string arg, string name, out string value)
    {
        var prefix = name + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new SeederUsageException($"{name} 缺少取值");
            }

            return true;
        }

        value = string.Empty;
        return false;
    }
}

/// <summary>命令行用法错误：打印帮助并返回退出码 2。</summary>
internal sealed class SeederUsageException(string message) : Exception(message);
