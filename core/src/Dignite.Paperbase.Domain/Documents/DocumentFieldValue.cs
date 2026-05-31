using System;
using System.Text.Json;
using Dignite.Paperbase.Documents.Fields;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 类型化的单字段值——<see cref="Document.SetFields"/> 的输入元素（字段架构 v2 / #207）。
/// <para>
/// App 层（<c>FieldExtractionEventHandler</c> / <c>DocumentAppService.UpdateExtractedFieldsAsync</c>）
/// 在校验通过后构造：拿到 LLM / 操作员提交的原始 <see cref="JsonElement"/> + 该字段所属
/// <c>FieldDefinition</c>（含 <see cref="FieldDefinitionId"/> 与 <see cref="DataType"/>），经
/// <c>ExtractedFieldValueValidator</c> 确认 <paramref name="Value"/> 与 <paramref name="DataType"/> 对齐后传给聚合根。
/// 聚合据此构造 / 更新 <see cref="DocumentExtractedField"/>（按 <see cref="FieldDefinitionId"/> reconcile，
/// JsonElement → typed 的转换集中在子实体内）。
/// </para>
/// <para>
/// <paramref name="Value"/> 必须是与 <paramref name="DataType"/> 对齐的规范 JSON <b>标量</b>形（数字裸 number、
/// 布尔 true/false、Date 为 <c>"yyyy-MM-dd"</c> 字符串、DateTime 为无偏移 <c>"yyyy-MM-ddThh:mm:ss"</c>
/// 字符串）；不对齐的值在 App 层已被滤掉 / loud fail，绝不到达此处。
/// </para>
/// <para>
/// <paramref name="Order"/>（#212）是该值在所属字段多值集合内的 0-based 位序，参与 <see cref="DocumentExtractedField"/>
/// 复合主键 <c>(DocumentId, FieldDefinitionId, Order)</c>。单值字段恒为 0；多值 String 字段（<c>FieldDefinition.AllowMultiple</c>）
/// 的 JSON 数组由 App 层拆成多个本记录（<c>Order = 0,1,2…</c>，每元素一个标量）。<see cref="Document.SetFields"/> 按
/// <c>(FieldDefinitionId, Order)</c> reconcile。
/// </para>
/// </summary>
public sealed record DocumentFieldValue(Guid FieldDefinitionId, FieldDataType DataType, JsonElement Value, int Order = 0);
