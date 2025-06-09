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

    public FinTubeActivityController(ILoggerFactory loggerFactory, IFileSystem fileSystem, IServerConfigurationManager config, IUserManager userManager, ILibraryManager libraryManager)
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
        public bool removenonmusic { get; set; } = false;
        public bool embedthumbnail { get; set; } = true;
        public bool embedmetadata { get; set; } = true;
    }

    /*
    This class is used to decode the SponsorBlock (SB) JSON reponse into a List<SponsorBlockSegment>.

    The SB API returns a JSON array with objects, each containing information about a sponsor segment.
    The only releveant property in each object is the segement start and end, which is given as another JSON array with two float values.

    This class thus has only the member segment.
    */
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

            // check for ffmpeg and ffprobe
            var ffmpegPath = Plugin.Instance?.FFmpegPath ?? "ffmpeg";

            String[] ffmpegPathSplit = ffmpegPath.Split("/");
            ffmpegPathSplit[ffmpegPathSplit.Length - 1] = "ffprobe";
            var ffprobePath = String.Join("/", ffmpegPathSplit);

            bool hasFFmpeg = System.IO.File.Exists(ffmpegPath) && System.IO.File.Exists(ffprobePath);
            if (data.removenonmusic && !hasFFmpeg)
                _logger.LogWarning($"FinTubeDownload : Built-in Jeyllfin FFmpeg not found, skipping SponsorBlock");

            // Ensure proper / separator
            data.targetfolder = String.Join("/", data.targetfolder.Split("/", StringSplitOptions.RemoveEmptyEntries));
            String targetPath = data.targetlibrary.EndsWith("/") ? data.targetlibrary + data.targetfolder : data.targetlibrary + "/" + data.targetfolder;
            // Create Folder if it doesn't exist
            if (!System.IO.Directory.CreateDirectory(targetPath).Exists)
                throw new Exception("Directory could not be created");

            // Check for tags
            bool hasTags = 1 < (data.title.Length + data.album.Length + data.artist.Length + data.track.ToString().Length);

            // Save file with ytdlp as mp4 or mp3 depending on audioonly
            String targetFilename;
            String targetExtension = (data.preferfreeformat ? (data.audioonly ? @".opus" : @".webm") : (data.audioonly ? @".mp3" : @".mp4"));

            if (data.audioonly && hasTags && data.title.Length > 1) // Use title Tag for filename
                targetFilename = System.IO.Path.Combine(targetPath, $"{data.title}");
            else // Use YTID as filename
                targetFilename = System.IO.Path.Combine(targetPath, $"{data.ytid}");

            // Check if filename exists
            if (System.IO.File.Exists(targetFilename))
                throw new Exception($"File {targetFilename} already exists");

            status += $"Filename: {targetFilename}<br>";

            String args = config.custom_ytdl_args;
            if (data.embedmetadata)
                args += " --embed-metadata";

            if (data.audioonly)
            {
                // Use the best audio format and let any necessary conversion with FFmpeg also use the best audio quality
                args += " -x --audio-quality 0 -f \"bestaudio/best\"";
                if (data.preferfreeformat)
                    args += " --prefer-free-format";
                else
                    args += " --audio-format mp3";

                if (data.embedthumbnail)
                    args += " --embed-thumbnail";

                args += $" -o \"{targetFilename}.%(ext)s\" {data.ytid}";
            }
            else
            {
                if (data.preferfreeformat)
                    args += " --prefer-free-format";
                else
                    args += " -f mp4";
                if (!string.IsNullOrEmpty(data.videoresolution))
                    args += $" -S res:{data.videoresolution}";
                args += $" -o \"{targetFilename}-%(title)s.%(ext)s\" {data.ytid}";
            }

            status += $"Exec: {config.exec_YTDL} {args}<br>";

            var procyt = createProcess(config.exec_YTDL, args);
            procyt.Start();
            procyt.WaitForExit();

            // If sponsorblock is active AND ffmpeg is available AND audio only download - Try to remove non-music segments
            if (data.removenonmusic && hasFFmpeg && data.audioonly)
            {
                // Get file duration with ffprobe
                // The SponsorBlock API also returns a video duration, but according to their docs, it may not be available for all videos
                args = $"-i \"{targetFilename}{targetExtension}\" -show_entries format=duration -v quiet -of csv=\"p=0\"";
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                };

                Process procFFprobe = new Process() { StartInfo = startInfo };
                procFFprobe.Start();

                float duration = float.Parse(procFFprobe.StandardOutput.ReadLine().Trim());
                procFFprobe.WaitForExit();

                if (procFFprobe.ExitCode != 0)
                    throw new Exception($"FFprobe was started with args {args} and failed with exit-code {procFFprobe.ExitCode}");

                // Fetch segments from server (only category non-music)
                using HttpClient httpClient = new HttpClient();

                var request = new HttpRequestMessage(HttpMethod.Get, $"https://sponsor.ajay.app/api/skipSegments?videoID={data.ytid}&category=music_offtopic");
                HttpResponseMessage sponsorResponse = httpClient.Send(request);

                if (sponsorResponse.StatusCode == HttpStatusCode.OK)
                {
                    // Decode the JSON response
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var segments = JsonSerializer.Deserialize<List<SponsorBlockSegment>>(sponsorResponse.Content.ReadAsStream(), options);

                    // In the following part the segments are "transformed".
                    // The SponsorBlock (SB) API just returns the timestamps for the start and the end of a non-music segement.
                    // I found it way more easy to tell FFmpeg which parts of the audio to keep and reassemble them to a new audio file,
                    // instead of telling FFmpeg which parts to avoid.
                    // Thus the SB segements, which are parts to avoid need to be transferred into parts to keep.

                    //list with parts to keep, each array in the list holds the start and the end of a part
                    List<float[]> contentSegments = new List<float[]>();

                    // The first segement from the API doesn't start at the beginning of the file (position 0),
                    // that means, that there is content to keep from pos 0 to the beginning of the first segment.
                    if (segments[0].segment[0] != 0)
                        contentSegments.Add(new float[2] { 0, segments[0].segment[0] });

                    // Now for all other segments (except the last one, that is handled below) the parts to keep are between the segements to avoid.
                    // So each content segment starts with the end of a non-music segement and ends with the start of the next one
                    int segmentCount = segments.Count - 1;
                    for (int i = 0; i < segmentCount; i++)
                        contentSegments.Add(new float[2] { segments[i].segment[1], segments[i + 1].segment[0] });

                    // If the last segement doesn't end with the end of the file,
                    // this means that there is content between the last segment's end and the end of the file
                    if (segments[segmentCount].segment[1] < duration)
                        contentSegments.Add(new float[2] { segments[segmentCount].segment[1], duration });

                    // To let FFmpeg built together a new file from the desired content segments,
                    // the filter option 'aselect' is used together with the between() function
                    // For each part of content to keep, FFMpeg needs a filter argument between(t,<content_start>,<content_end>)
                    // see https://ffmpeg.org/ffmpeg-filters.html#select_002c-aselect
                    // the 't' is for the format of content_start and content_end (t meaning these values are given in seconds (with fractions))
                    List<String> ffmpegBetweenStrings = new List<String>();
                    foreach (float[] segment in contentSegments)
                        // Use culture invariant here, as FFmpeg expects a dot as decimal seperator
                        ffmpegBetweenStrings.Add($"between(t,{segment[0].ToString(CultureInfo.InvariantCulture)},{segment[1].ToString(CultureInfo.InvariantCulture)})");

                    args = $"-i \"{targetFilename}{targetExtension}\" -af \"aselect='{String.Join("+", ffmpegBetweenStrings.ToArray())}',asetpts=N/SR/TB\"";
                    // Add nmr (non-music removed) to the target filename, this temporary file will be deleted, after ffmpeg finishes
                    args += $" \"{targetFilename}-nmr{targetExtension}\"";

                    Process procFFmpeg = createProcess(ffmpegPath, args);
                    procFFmpeg.Start();
                    procFFmpeg.WaitForExit();

                    if (procFFmpeg.ExitCode != 0)
                        throw new Exception($"FFmpeg was started with args {args} and failed with exit-code {procFFmpeg.ExitCode}");

                    // Delete the original file and move the non-music removed file into it's place
                    System.IO.File.Delete($"{targetFilename}{targetExtension}");
                    System.IO.File.Move($"{targetFilename}-nmr{targetExtension}", $"{targetFilename}{targetExtension}");
                }
            }

            // If audioonly AND id3v2 AND tags are set - Tag the mp3 file
            if (data.audioonly && hasid3v2 && hasTags)
            {
                args = $"-a \"{data.artist}\" -A \"{data.album}\" -t \"{data.title}\" -T \"{data.track}\" \"{targetFilename}{targetExtension}\"";

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
            _logger.LogInformation("FinTubeDLibraries count: {count}", _libraryManager.GetVirtualFolders().Count);

            Dictionary<string, object> response = new Dictionary<string, object>();
            response.Add("data", _libraryManager.GetVirtualFolders().Select(i => i.Locations).ToArray());
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
