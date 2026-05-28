using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Slugging;
using Microsoft.AspNetCore.Mvc;

namespace Dignite.Paperbase.HttpApi.Slugging;

[Area("paperbase")]
[Route("api/paperbase/slug-suggestion")]
public class SlugSuggestionController : PaperbaseController, ISlugSuggestionAppService
{
    private readonly ISlugSuggestionAppService _slugSuggestionAppService;

    public SlugSuggestionController(ISlugSuggestionAppService slugSuggestionAppService)
    {
        _slugSuggestionAppService = slugSuggestionAppService;
    }

    [HttpPost("suggest")]
    public virtual Task<SlugSuggestionDto> SuggestAsync([FromBody] SuggestSlugInput input, CancellationToken cancellationToken)
    {
        return _slugSuggestionAppService.SuggestAsync(input, cancellationToken);
    }
}
