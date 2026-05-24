import { Injectable, inject } from '@angular/core';
import { RestService } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type {
  SlugSuggestionDto,
  SuggestSlugInput,
} from '../../documents/slug-suggestion.models';

// Backend: Dignite.Paperbase.HttpApi.Documents.SlugSuggestionController (/api/paperbase/slug-suggestion).
@Injectable({ providedIn: 'root' })
export class SlugSuggestionService {
  private readonly rest = inject(RestService);
  private readonly apiName = 'Default';
  private readonly basePath = '/api/paperbase/slug-suggestion';

  // 显示名 → 机器标识建议（LLM 英译 + 服务端 sanitize）。
  suggest = (input: SuggestSlugInput): Observable<SlugSuggestionDto> =>
    this.rest.request<SuggestSlugInput, SlugSuggestionDto>(
      { method: 'POST', url: `${this.basePath}/suggest`, body: input },
      { apiName: this.apiName },
    );
}
