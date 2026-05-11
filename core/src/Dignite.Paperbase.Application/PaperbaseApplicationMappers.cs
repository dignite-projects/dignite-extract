using Dignite.Paperbase.Chat;
using Dignite.Paperbase.Documents;
using Riok.Mapperly.Abstractions;
using Volo.Abp.Mapperly;

namespace Dignite.Paperbase;

/// <summary>
/// Document -> DocumentDto
/// FileOrigin and PipelineRun nested mappings are consolidated here (Mapperly compile-time constraint).
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentDtoMapper : MapperBase<Document, DocumentDto>
{
    [UseMapper]
    private readonly DocumentPipelineRunToDocumentPipelineRunDtoMapper _pipelineRunMapper = new();

    public override partial DocumentDto Map(Document source);
    public override partial void Map(Document source, DocumentDto destination);
}

/// <summary>
/// DocumentPipelineRun -> DocumentPipelineRunDto.
/// 带 <see cref="MapExtraPropertiesAttribute"/>：把 <c>ExtraProperties</c>（如分类候选 top-K）
/// 透传到 DTO，让前端按约定 key 读取各 pipeline 的产物。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
[MapExtraProperties]
public partial class DocumentPipelineRunToDocumentPipelineRunDtoMapper : MapperBase<DocumentPipelineRun, DocumentPipelineRunDto>
{
    public override partial DocumentPipelineRunDto Map(DocumentPipelineRun source);
    public override partial void Map(DocumentPipelineRun source, DocumentPipelineRunDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentListItemDtoMapper : MapperBase<Document, DocumentListItemDto>
{
    public override partial DocumentListItemDto Map(Document source);
    public override partial void Map(Document source, DocumentListItemDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentRelationToDocumentRelationDtoMapper : MapperBase<DocumentRelation, DocumentRelationDto>
{
    public override partial DocumentRelationDto Map(DocumentRelation source);
    public override partial void Map(DocumentRelation source, DocumentRelationDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class ChatConversationToChatConversationDtoMapper
    : MapperBase<ChatConversation, ChatConversationDto>
{
    public override partial ChatConversationDto Map(ChatConversation source);
    public override partial void Map(ChatConversation source, ChatConversationDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class ChatConversationToChatConversationListItemDtoMapper
    : MapperBase<ChatConversation, ChatConversationListItemDto>
{
    public override partial ChatConversationListItemDto Map(ChatConversation source);
    public override partial void Map(ChatConversation source, ChatConversationListItemDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class ChatMessageToChatMessageDtoMapper
    : MapperBase<ChatMessage, ChatMessageDto>
{
    public override partial ChatMessageDto Map(ChatMessage source);
    public override partial void Map(ChatMessage source, ChatMessageDto destination);
}
