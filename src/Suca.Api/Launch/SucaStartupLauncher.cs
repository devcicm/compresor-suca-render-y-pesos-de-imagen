using System;
using System.Collections.Generic;
using System.Linq;

namespace SUCA_Compressor
{
    public enum SucaLaunchMode
    {
        Console,
        Web,
        Help,
        Cancelled
    }

    public sealed class SucaLaunchSelection
    {
        public SucaLaunchMode Mode { get; init; }
        public string[] WebHostArguments { get; init; } = Array.Empty<string>();
    }

    public static class SucaStartupLauncher
    {
        public static SucaLaunchSelection Select(string[]? args)
        {
            args ??= Array.Empty<string>();
            var requestedModes = new HashSet<SucaLaunchMode>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (IsHelp(arg))
                {
                    return new SucaLaunchSelection { Mode = SucaLaunchMode.Help };
                }

                if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("Falta el valor de --mode. Use console o web.");

                    requestedModes.Add(ParseMode(args[++i]));
                    continue;
                }

                if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))
                {
                    requestedModes.Add(ParseMode(arg.Substring("--mode=".Length)));
                    continue;
                }

                if (IsConsoleAlias(arg))
                    requestedModes.Add(SucaLaunchMode.Console);
                else if (IsWebAlias(arg))
                    requestedModes.Add(SucaLaunchMode.Web);
            }

            if (requestedModes.Count > 1)
                throw new ArgumentException("Solo puede elegir un modo de arranque: console o web.");

            SucaLaunchMode mode = requestedModes.Count == 1
                ? requestedModes.Single()
                : AskForMode();

            return new SucaLaunchSelection
            {
                Mode = mode,
                WebHostArguments = RemoveLauncherArguments(args)
            };
        }

        public static void PrintUsage()
        {
            Console.WriteLine("SUCA launcher");
            Console.WriteLine("  dotnet run --project \"ConsoleApp suca compress.csproj\" -- --mode console --input <imagen> [--output <carpeta>]");
            Console.WriteLine("  dotnet run --project \"ConsoleApp suca compress.csproj\" -- --mode web [--urls http://127.0.0.1:5077]");
            Console.WriteLine();
            Console.WriteLine("Aliases: console, --console, web, --web, serve y --rest.");
            Console.WriteLine("Sin argumentos, el launcher muestra una ventana para elegir el modo.");
        }

        private static SucaLaunchMode AskForMode()
        {
            if (Console.IsInputRedirected)
                throw new ArgumentException("Especifique --mode console o --mode web cuando la entrada no sea interactiva.");

            return SucaLaunchDialog.ShowModeDialog() ?? SucaLaunchMode.Cancelled;
        }

        private static SucaLaunchMode ParseMode(string value)
        {
            if (IsConsoleAlias(value))
                return SucaLaunchMode.Console;
            if (IsWebAlias(value))
                return SucaLaunchMode.Web;

            throw new ArgumentException($"Modo no valido: {value}. Use console o web.");
        }

        private static string[] RemoveLauncherArguments(string[] args)
        {
            var forwarded = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase) ||
                    IsConsoleAlias(arg) ||
                    IsWebAlias(arg))
                    continue;

                forwarded.Add(arg);
            }

            return forwarded.ToArray();
        }

        private static bool IsHelp(string value) =>
            string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

        private static bool IsConsoleAlias(string value) =>
            string.Equals(value, "console", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "--console", StringComparison.OrdinalIgnoreCase);

        private static bool IsWebAlias(string value) =>
            string.Equals(value, "web", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "--web", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "serve", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "--rest", StringComparison.OrdinalIgnoreCase);
    }
}
