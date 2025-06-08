using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Data.Entities;
using Jellyfin.Plugin.FinTube.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTube.Api;

[ApiController]
[Authorize(Roles = "Administrator")]
[Route("fintube")]
[Produces(MediaTypeNames.Application.Json)]
public class FinTubeActivityController : ControllerBase
{
    private readonly ILogger<FinTubeActivityController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IFileSystem _fileSystem;
    private readonly IServerConfigurationManager _config;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    public FinTubeActivityController(
        ILoggerFactory loggerFactory,
        IFileSystem fileSystem,
        IServerConfigurationManager config,
        IUserManager userManager,
        ILibraryManager libraryManager
    )
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<FinTubeActivityController>();
        _fileSystem = fileSystem;
        _config = config;
        _userManager = userManager;
        _libraryManager = libraryManager;

        _logger.LogInformation("FinTubeActivityController Loaded");
    }

    public class FinTubeData
    {
        public string ytid { get; set; } = "";
        public string targetlibrary { get; set; } = "";
        public string targetfolder { get; set; } = "";
        public bool audioonly { get; set; } = false;
        public bool preferfreeformat { get; set; } = false;
        public string videoresolution { get; set; } = "";
        public string artist { get; set; } = "";
        public string album { get; set; } = "";
        public string title { get; set; } = "";
        public int track { get; set; } = 0;
    }

    public class SponsorBlockSegment
    {
        public List<float> segment { get; set; } = new List<float>();
    }

    [HttpPost("submit_dl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, object>> FinTubeDownload([FromBody] FinTubeData data)
    {
        try
        {
            _logger.LogInformation(
                "FinTubeDownload : {ytid} to {targetfoldeer}, prefer free format: {preferfreeformat} audio only: {audioonly}",
                data.ytid,
                data.targetfolder,
                data.preferfreeformat,
                data.audioonly
            );

            Dictionary<string, object> response = new Dictionary<string, object>();
            PluginConfiguration? config = Plugin.Instance.Configuration;
            String status = "";

            // check binaries
            if (!System.IO.File.Exists(config.exec_YTDL))
                throw new Exception("YT-DL Executable configured incorrectly");

            bool hasid3v2 = System.IO.File.Exists(config.exec_ID3);
            bool hasFFMpeg = System.IO.File.Exists(config.exec_FFMPEG);

            // Ensure proper / separator
            data.targetfolder = String.Join(
                "/",
                data.targetfolder.Split("/", StringSplitOptions.RemoveEmptyEntries)
            );
            String targetPath = data.targetlibrary.EndsWith("/")
                ? data.targetlibrary + data.targetfolder
                : data.targetlibrary + "/" + data.targetfolder;
            // Create Folder if it doesn't exist
            if (!System.IO.Directory.CreateDirectory(targetPath).Exists)
                throw new Exception("Directory could not be created");

            // Check for tags
            bool hasTags =
                1
                < (
                    data.title.Length
                    + data.album.Length
                    + data.artist.Length
                    + data.track.ToString().Length
                );

            // Save file with ytdlp as mp4 or mp3 depending on audioonly
            String targetFilename;
            String targetExtension = (
                data.preferfreeformat
                    ? (data.audioonly ? @".opus" : @".webm")
                    : (data.audioonly ? @".mp3" : @".mp4")
            );

            if (data.audioonly && hasTags && data.title.Length > 1) // Use title Tag for filename
                targetFilename = System.IO.Path.Combine(targetPath, $"{data.title}");
            else // Use YTID as filename
                targetFilename = System.IO.Path.Combine(targetPath, $"{data.ytid}");

            // Check if filename exists
            if (System.IO.File.Exists(targetFilename))
                throw new Exception($"File {targetFilename} already exists");

            status += $"Filename: {targetFilename}<br>";

            String args;
            if (data.audioonly)
            {
                args = "-x";
                if (data.preferfreeformat)
                    args += " --prefer-free-format";
                else
                    args += " --audio-format mp3";
                args += $" -o \"{targetFilename}.%(ext)s\" {data.ytid}";
            }
            else
            {
                if (data.preferfreeformat)
                    args = "--prefer-free-format";
                else
                    args = "-f mp4";
                if (!string.IsNullOrEmpty(data.videoresolution))
                    args += $" -S res:{data.videoresolution}";
                args += $" -o \"{targetFilename}-%(title)s.%(ext)s\" {data.ytid}";
            }

            status += $"Exec: {config.exec_YTDL} {args}<br>";

            var procyt = createProcess(config.exec_YTDL, args);
            procyt.Start();
            procyt.WaitForExit();

            // If sponsorblock is active AND ffmpeg is available - Try to remove non-music segments
            if (hasFFMpeg && data.audioonly)
            {
                // Get file duration with ffprobe
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/ffprobe",
                    Arguments =
                        $"-i \"{targetFilename}{targetExtension}\" -show_entries format=duration -v quiet -of csv=\"p=0\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };
                Process procFfprobe = new Process() { StartInfo = startInfo };
                procFfprobe.Start();
                int duration = int.Parse(
                    procFfprobe.StandardOutput.ReadLine().Trim().Split('.')[0]
                );
                procFfprobe.WaitForExit();

                if (procFfprobe.ExitCode != 0)
                    throw new Exception($"Ffprobe failed with code {procFfprobe.ExitCode}");

                // Fetch segments from server
                using HttpClient httpClient = new HttpClient();

                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://sponsor.ajay.app/api/skipSegments?videoID={data.ytid}&category=music_offtopic"
                );
                HttpResponseMessage sponsorResponse = httpClient.Send(request);

                if (sponsorResponse.StatusCode == HttpStatusCode.OK)
                {
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var segments = JsonSerializer.Deserialize<List<SponsorBlockSegment>>(
                        sponsorResponse.Content.ReadAsStream(),
                        options
                    );

                    List<float[]> videoSegments = new List<float[]>();

                    //begin to first segment is video part, add it
                    if (segments[0].segment[0] != 0)
                        videoSegments.Add(new float[2] { 0, segments[0].segment[0] });

                    int segmentCount = segments.Count - 1;
                    for (int i = 0; i < segmentCount; i++)
                    {
                        videoSegments.Add(
                            new float[2] { segments[i].segment[1], segments[i + 1].segment[0] }
                        );
                    }

                    //same as above for beginning, but for the end
                    if (segments[segmentCount].segment[1] < duration)
                        videoSegments.Add(
                            new float[2] { segments[segmentCount].segment[1], duration }
                        );

                    List<String> ffmpegBetweenStrings = new List<String>();
                    foreach (float[] segment in videoSegments)
                        ffmpegBetweenStrings.Add(
                            $"between(t,{segment[0].ToString(CultureInfo.InvariantCulture)},{segment[1].ToString(CultureInfo.InvariantCulture)})"
                        );

                    args =
                        $"-i \"{targetFilename}{targetExtension}\" -af \"aselect='{String.Join("+", ffmpegBetweenStrings.ToArray())}',asetpts=N/SR/TB\"";
                    args += $" \"{targetFilename}-nmr{targetExtension}\"";

                    Process procFfmpeg = createProcess(config.exec_FFMPEG, args);
                    procFfmpeg.Start();
                    procFfmpeg.WaitForExit();

                    if (procFfmpeg.ExitCode != 0)
                        throw new Exception($"FFMpeg failed with code {procFfmpeg.ExitCode}");
                }
            }

            // If audioonly AND id3v2 AND tags are set - Tag the mp3 file
            if (data.audioonly && hasid3v2 && hasTags)
            {
                args =
                    $"-a \"{data.artist}\" -A \"{data.album}\" -t \"{data.title}\" -T \"{data.track}\" \"{targetFilename}{targetExtension}\"";

                status += $"Exec: {config.exec_ID3} {args}<br>";

                var procid3 = createProcess(config.exec_ID3, args);
                procid3.Start();
                procid3.WaitForExit();
            }

            status += "<font color='green'>File Saved!</font>";

            response.Add("message", status);
            return Ok(response);
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            return StatusCode(500, new Dictionary<string, object>() { { "message", e.Message } });
        }
    }

    [HttpGet("libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Dictionary<string, object>> FinTubeLibraries()
    {
        try
        {
            _logger.LogInformation(
                "FinTubeDLibraries count: {count}",
                _libraryManager.GetVirtualFolders().Count
            );

            Dictionary<string, object> response = new Dictionary<string, object>();
            response.Add(
                "data",
                _libraryManager.GetVirtualFolders().Select(i => i.Locations).ToArray()
            );
            return Ok(response);
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            return StatusCode(500, new Dictionary<string, object>() { { "message", e.Message } });
        }
    }

    private static Process createProcess(String exe, String args)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo() { FileName = exe, Arguments = args };
        return new Process() { StartInfo = startInfo };
    }
}
