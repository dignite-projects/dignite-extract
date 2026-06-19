using Dignite.Extract.Abstractions;
using Dignite.Extract.Ocr;
using Volo.Abp.Modularity;

namespace Dignite.Extract.Parse;

[DependsOn(typeof(ExtractAbstractionsModule), typeof(ExtractOcrModule))]
public class ExtractParseModule : AbpModule
{
}
