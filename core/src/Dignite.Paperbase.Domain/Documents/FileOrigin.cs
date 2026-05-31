using System.Collections.Generic;
using Volo.Abp;
using Volo.Abp.Domain.Values;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 文件来源信息。写入后不可修改，是系统信任的锚点。
/// </summary>
public class FileOrigin : ValueObject
{
    /// <summary>BlobStore 中的 Key，写入后不可修改</summary>
    public string BlobName { get; private set; } = default!;

    /// <summary>上传操作人名称快照（冗余存储，防止用户删除后丢失信息）</summary>
    public string UploadedByUserName { get; private set; } = default!;

    /// <summary>原始文件名</summary>
    public string? OriginalFileName { get; private set; }

    /// <summary>文件 MIME 类型</summary>
    public string ContentType { get; private set; } = default!;

    /// <summary>文件内容 SHA-256 哈希（十六进制小写，长度 64）。用于每租户范围内的字节级去重。</summary>
    public string ContentHash { get; private set; } = default!;

    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; private set; }

    protected FileOrigin() { }

    public FileOrigin(
        string blobName,
        string uploadedByUserName,
        string contentType,
        string contentHash,
        long fileSize,
        string? originalFileName = null)
    {
        BlobName = Check.NotNullOrWhiteSpace(blobName, nameof(blobName), FileOriginConsts.MaxBlobNameLength);
        UploadedByUserName = Check.NotNullOrWhiteSpace(
            uploadedByUserName,
            nameof(uploadedByUserName),
            FileOriginConsts.MaxUploadedByUserNameLength);
        ContentType = Check.NotNullOrWhiteSpace(contentType, nameof(contentType), FileOriginConsts.MaxContentTypeLength);
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash), FileOriginConsts.MaxContentHashLength);
        FileSize = Check.Range(fileSize, nameof(fileSize), 0, long.MaxValue);
        OriginalFileName = NormalizeOptionalString(originalFileName, nameof(originalFileName), FileOriginConsts.MaxOriginalFileNameLength);
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return BlobName;
        yield return UploadedByUserName;
        yield return OriginalFileName ?? string.Empty;
        yield return ContentType;
        yield return ContentHash;
        yield return FileSize;
    }

    private static string? NormalizeOptionalString(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            throw new AbpException($"{parameterName} can not be longer than {maxLength} characters.");
        }

        return value;
    }
}
