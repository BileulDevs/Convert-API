namespace ConvertApi.Core.Formats;

/// <summary>A group of formats handled by the same conversion engine, with its own size limit.</summary>
public enum FormatFamily
{
    Audio,
    Video,
    Image,
    Document,
}
