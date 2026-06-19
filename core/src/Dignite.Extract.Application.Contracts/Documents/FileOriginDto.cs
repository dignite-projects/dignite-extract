namespace Dignite.Extract.Documents;

public class FileOriginDto
{
    public string UploadedByUserName { get; set; } = default!;
    public string? OriginalFileName { get; set; }
    public string ContentType { get; set; } = default!;
    public long FileSize { get; set; }
}
