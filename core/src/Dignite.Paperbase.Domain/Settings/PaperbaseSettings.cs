namespace Dignite.Paperbase.Settings;

public static class PaperbaseSettings
{
    public const string GroupName = "Paperbase";

    /// <summary>
    /// OCR 置信度门槛。<see cref="Dignite.Paperbase.Documents.DocumentReadyEto"/> 发布前
    /// 必须 ≥ 此值（或操作员手动通过审核）；不达标的文档进待人工审核队列。
    /// per-tenant 可通过 ABP Setting Management API 覆盖；Host 默认在
    /// <c>PaperbaseSettingDefinitionProvider</c> 中注册。
    /// </summary>
    public const string OcrConfidenceThreshold = GroupName + ".Ocr.ConfidenceThreshold";
}
