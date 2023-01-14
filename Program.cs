using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

using SharpCompress.Readers;

namespace AutoVerifyTAS;

public static class StringExtensions
{
    public static string RemoveWhitespace(this string str)
        => string.Join(string.Empty, str.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
}

public static class HttpSingleton
{
    public static HttpClient Http { get; } = new();
}

public interface ISubmissionInfo
{
    /// <summary>
    /// Raw movie file, generally will need some parsing
    /// </summary>
    Stream MovieFile { get; }
    
    /// <summary>summary
    /// Movie extension, should be used to determine movie format
    /// </summary>
    string MovieExtension { get; }
    
    /// <summary>
    /// Submission reported emulator version, generally should only
    /// be used if the movie file not does have the emulator version
    /// </summary>
    string EmulatorVersion { get; }
}

public class SubmissionInfo : ISubmissionInfo
{
    public Stream MovieFile { get; private init; }
    public string MovieExtension { get; private init; }
    public string EmulatorVersion { get; private init; }

    private const string SubmissionUrlBase = "https://tasvideos.org/api/v1/Submissions/";
    private const string MovieFileBase = "https://tasvideos.org/{0}S?handler=Download";

    public static async Task<ISubmissionInfo> GetSubmissionInfo(int submissionNumber)
    {
        var movieFileZip = await HttpSingleton.Http
            .GetByteArrayAsync(string.Format(MovieFileBase, submissionNumber))
            .ConfigureAwait(false);

        await using var movieFileZipStream = new MemoryStream(movieFileZip);
        using var zip = new ZipArchive(movieFileZipStream);
        if (zip.Entries.Count != 1)
        {
            throw new InvalidOperationException("Movie .zips should only have 1 file, how did this happen?");
        }

        var movieFile = new MemoryStream();
        await zip.Entries[0].Open().CopyToAsync(movieFile);
        movieFile.Seek(0, SeekOrigin.Begin);
        
        var submissionJson = await HttpSingleton.Http
            .GetFromJsonAsync<Dictionary<string, JsonElement>>(SubmissionUrlBase + submissionNumber)
            .ConfigureAwait(false);

        return new SubmissionInfo
        {
            MovieFile = movieFile,
            MovieExtension = submissionJson["movieExtension"].GetString(),
            EmulatorVersion = submissionJson["emulatorVersion"].GetString(),
        };
    }
}

public class DisposableFile : IDisposable
{
    public string Path { get; }
    private readonly bool _isTempFile;

    private const string TempFolder = "/tmp/autojudgetas";
    
    static DisposableFile()
        => Directory.CreateDirectory(TempFolder);

    public DisposableFile(Stream file, string name)
    {
        Path = $"{TempFolder}/{Guid.NewGuid()}_{name}";
        using var fs = File.OpenWrite(Path);
        file.Seek(0, SeekOrigin.Begin);
        file.CopyTo(fs);
        _isTempFile = true;
    }
    
    public DisposableFile(byte[] file, string name)
    {
        Path = $"{TempFolder}/{Guid.NewGuid()}_{name}";
        File.WriteAllBytes(Path, file);
        _isTempFile = true;
    }
    
    public DisposableFile(string path)
    {
        Path = path;
        _isTempFile = false;
    }

    public void Dispose()
    {
        if (_isTempFile)
        {
            File.Delete(Path);
        }
        
        GC.SuppressFinalize(this);
    }
}

public interface IParsedMovie : IDisposable
{
    /// <summary>
    /// Movie file, may be stored in a temp location
    /// </summary>
    DisposableFile MovieFile { get; }
    
    /// <summary>
    /// Game name from the movie file
    /// </summary>
    string GameName { get; }
    
    /// <summary>
    /// Setup the environment for running the movie
    /// </summary>
    /// <param name="path">empty folder to setup the environment</param>
    /// <returns>Task representing setup work</returns>
    Task SetupEnvironment(string path);
    
    /// <summary>
    /// Run the emulator with the movie file
    /// </summary>
    /// <param name="movieRom">movie rom used for movie</param>
    /// <returns>Task representing emulator running</returns>
    Task RunEmulator(DisposableFile movieRom);
}

public static class MovieParseFactory
{
    private static Task DoProcess(string fileName, string arguments, bool createNoWindow, string workingDir)
        => Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = createNoWindow,
            WorkingDirectory = workingDir,
        })!.WaitForExitAsync();

    private class ParsedLtm : IParsedMovie
    {
        public DisposableFile MovieFile { get; }
        public string GameName { get; }
        
        private readonly string _libTasTag;
        private readonly string _ruffleTag;
        private string _path;

        public ParsedLtm(Stream ltm, string gameName, string libTasTag, string ruffleTag)
        {
            MovieFile = new(ltm, gameName + ".ltm");
            GameName = gameName;
            _libTasTag = libTasTag;
            _ruffleTag = ruffleTag;
        }

        private async Task SetupRuffle(string path)
        {
            if (_ruffleTag is null) return;
            // need to build Ruffle (as the downloads are not reliable due to dependency shenanigans)
            await DoProcess("git", $"clone -b {_ruffleTag} https://github.com/ruffle-rs/ruffle", true, path).ConfigureAwait(false);
            await DoProcess("cargo", "build --release --package=ruffle_desktop", true, Path.Combine(path, "ruffle")).ConfigureAwait(false);
            File.Move(Path.Combine(path, "ruffle/target/release/ruffle_desktop"), Path.Combine(path, "ruffle_desktop"));
        }
        
        private async Task SetupLibTas(string path)
        {
            // best build libTas, don't want to deal with issues with .deb files...
            await DoProcess("git", $"clone -b {_libTasTag} https://github.com/clementgallet/libTAS", true, path).ConfigureAwait(false);
            await DoProcess("sh", "build.sh --disable-hud --with-i386 --disable-build-date", true, Path.Combine(path, "libTAS")).ConfigureAwait(false);
            File.Move(Path.Combine(path, "libTAS/src/library/libtas.so"), Path.Combine(path, "libtas.so"));
            File.Move(Path.Combine(path, "libTAS/src/library/libtas32.so"), Path.Combine(path, "libtas32.so"));
            File.Move(Path.Combine(path, "libTAS/src/program/libTAS"), Path.Combine(path, "libTAS.elf"));
        }

        public async Task SetupEnvironment(string path)
        {
            var ruffleTask = SetupRuffle(path);
            await SetupLibTas(path).ConfigureAwait(false);
            await ruffleTask.ConfigureAwait(false);
            _path = path;
        }
        
        public async Task RunEmulator(DisposableFile movieRom)
        {
            // headless, run movie
            var baseArgs = $"-r {MovieFile.Path}";
            var isRuffle = _ruffleTag is not null;
            var execFile = isRuffle ? "ruffle_desktop" : movieRom.Path;
            var cliArgs = isRuffle ? movieRom.Path : string.Empty; // TODO: add way to add special CLI args (autodetect common ones perhaps?)
            await DoProcess($"{_path}/libTAS.elf", $"{baseArgs} {execFile} {cliArgs}", false, _path).ConfigureAwait(false);
        }

        public void Dispose()
        {
            MovieFile.Dispose();
        }
    }
    
    private static async Task<IParsedMovie> ParseLtm(Stream ltm, string submissionEmulatorVersion)
    {
        (int Major, int Minor, int Patch) libTasVersion = (-1, -1, -1);
        string ruffleTag = null;
        string gameName = null;
        using var reader = ReaderFactory.Open(ltm);
        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            await using var entry = reader.OpenEntryStream();
            using var textReader = new StreamReader(entry);
            switch (reader.Entry.Key)
            {
                case "config.ini":
                    while (await textReader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        var split = line.Split(new[] { "=" }, StringSplitOptions.RemoveEmptyEntries);
                        if (split.Length < 2) continue;

                        switch (split[0])
                        {
                            case "libtas_major_version":
                                libTasVersion.Major = int.Parse(split[1]);
                                break;
                            case "libtas_minor_version":
                                libTasVersion.Minor = int.Parse(split[1]);
                                break;
                            case "libtas_patch_version":
                                libTasVersion.Patch = int.Parse(split[1]);
                                break;
                            case "game_name":
                                gameName = split[1];
                                break;
                        }
                    }
                    
                    break;
                case "annotations.txt":
                    while (await textReader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (line.RemoveWhitespace().ToLower() is not "platform:flash") continue;
                        
                        // for Flash, we need to know the Ruffle revision to use, but there isn't
                        // a clear cut way to do that with just the movie file. the movie file will
                        // at best contain a hash of the executable. but only way to figure out which
                        // version that corresponds to is checking every release, and there are way
                        // too many to go through for that approach. luckily, it's fairly standard
                        // practice to put in the Ruffle version in the submission emulator version
                        // field, so we'll parse the date from that and get the exact revision that way

                        var dateStart = submissionEmulatorVersion.IndexOf("2022", StringComparison.Ordinal);
                        if (dateStart == -1) dateStart = submissionEmulatorVersion.IndexOf("2023", StringComparison.Ordinal);
                        
                        var ruffleDate = submissionEmulatorVersion.AsSpan().Slice(dateStart, 10).ToArray();
                        ruffleDate[4] = '-'; // 202x-
                        ruffleDate[7] = '-'; // 202x-xx-
                        
                        ruffleTag = $"nightly-{new string(ruffleDate)}";
                        break;
                    }
                    
                    break;
            }
            
            entry.SkipEntry();
        }
        
        var libTasTag = $"v{libTasVersion.Major}.{libTasVersion.Minor}.{libTasVersion.Patch}";
        return new ParsedLtm(ltm, gameName, libTasTag, ruffleTag);
    }
    
    public static async Task<IParsedMovie> ParseMovieInfo(ISubmissionInfo submissionInfo) 
        => submissionInfo.MovieExtension switch
        {
            "ltm" => await ParseLtm(submissionInfo.MovieFile, submissionInfo.EmulatorVersion).ConfigureAwait(false),
            _ => null,
        };
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("A submission number and rom file path must be provided");
            return -1;
        }

        var submissionInfo = await SubmissionInfo
            .GetSubmissionInfo(int.Parse(args[0]))
            .ConfigureAwait(false);

        using DisposableFile romFile = Directory.Exists(args[1])
            ? new(args[1]) 
            : new(await File
                .ReadAllBytesAsync(args[1])
                .ConfigureAwait(false),
                Path.GetFileName(args[1]));

        using var parsedMovie = await MovieParseFactory
            .ParseMovieInfo(submissionInfo)
            .ConfigureAwait(false);

        var path = $"{Guid.NewGuid()}_{parsedMovie.GameName.RemoveWhitespace()}";
        Directory.CreateDirectory(path);
        await parsedMovie.SetupEnvironment(path).ConfigureAwait(false);
        await parsedMovie.RunEmulator(romFile).ConfigureAwait(false);
        
        return 0;
    }
}
