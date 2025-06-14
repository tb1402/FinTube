using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FinTube.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        exec_YTDL = "/usr/local/bin/yt-dlp";
        exec_ID3 = "/usr/bin/id3v2";
        custom_ytdl_args = "";
        custom_ytdl_output_template = "";
    }

    /// <summary>
    /// Executable for youtube-dl/youtube-dlp
    /// </summary>
    public string exec_YTDL { get; set; }

    /// <summary>
    /// Executable for ID3v2
    /// </summary>
    public string exec_ID3 { get; set; }

    /// <summary>
    /// Custom args for ytdl
    /// </summary>
    public string custom_ytdl_args { get; set; }

    /// <summary>
    /// Custom output template for ytdl
    /// </summary>
    public string custom_ytdl_output_template { get; set; }
}
