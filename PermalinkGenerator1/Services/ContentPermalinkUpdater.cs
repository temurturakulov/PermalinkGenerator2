
using FM.Cinema.Core.Domain.Entities.Public;
using FM.Cinema.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.RegularExpressions;
using TranslitSharp;

namespace PermalinkGenerator1.Services;

public class ContentPermalinkUpdater(
    ApplicationDbContext dbContext,
    ILogger<ContentPermalinkUpdater> logger)
{
    public async Task UpdatePermalinksAsync(CancellationToken cancellationToken)
    {
        try
        {
            const int batchSize = 200;
            var updatedCount = 0;
            var page = 0;

            while (true)
            {
                var contents = await dbContext.Contents
                    .AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Skip(page * batchSize)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (contents.Count == 0)
                    break;

                logger.LogInformation("Processing batch {Page}: {Count} items", page + 1, contents.Count);

                var contentToPermalinks = new Dictionary<Content, string>();

                foreach (var item in contents)
                {
                    var basePermalink = GeneratePermalink(item);

                    if (string.IsNullOrWhiteSpace(basePermalink))
                    {
                        logger.LogWarning("Skipping Id={Id}: could not generate base permalink", item.Id);
                        continue;
                    }

                    contentToPermalinks[item] = basePermalink;
                }

                var permalinksToCheck = contentToPermalinks.Values
                    .Select(p =>
                        string.IsNullOrWhiteSpace(p) ? null : p.Trim().ToLowerInvariant())
                    .Where(p => p != null)
                    .ToList();

                var existingPermalinks = await dbContext.Contents
                    .AsNoTracking()
                    .Where(x => permalinksToCheck.Contains(x.Permalink.ToLower()))
                    .Select(x => x.Permalink.ToLowerInvariant())
                    .ToListAsync(cancellationToken);

                var updatedContents = new List<Content>();

                foreach (var (item, basePermalink) in contentToPermalinks)
                {
                    if (existingPermalinks.Contains(basePermalink.ToLower(CultureInfo.InvariantCulture)))
                    {
                        logger.LogWarning("Skipping Id={Id}: permalink '{Permalink}' already exists", item.Id, basePermalink);
                        continue;
                    }

                    if (item.Permalink == basePermalink)
                        continue;

                    logger.LogInformation("Updating Id={Id}: {Old} -> {New}", item.Id, item.Permalink, basePermalink);

                    item.Permalink = basePermalink;
                    updatedContents.Add(item);

                    existingPermalinks.Add(basePermalink.ToLower(CultureInfo.InvariantCulture));
                }

                dbContext.Contents.UpdateRange(updatedContents);
                var saved = await dbContext.SaveChangesAsync(cancellationToken);
                updatedCount += saved;

                logger.LogInformation("Batch {Page} saved {SavedCount} changes", page + 1, saved);

                page++;
            }

            logger.LogInformation("Total updated records: {TotalUpdated}", updatedCount);
        }
        catch (Exception e)
        {
            logger.LogError("Error message: {e}", e.Message);
        }
    }

    private string GeneratePermalink(Content item)
    {
        var text1 = "Hello 안녕하세요";
        var text2 = "Hello World";
        Console.WriteLine(ContainsKorean(text1));
        Console.WriteLine(ContainsKorean(text2));
        string? raw = null;

        if (!string.IsNullOrWhiteSpace(item.NameEn) && !ContainsKorean(item.NameEn))
            raw = item.NameEn;

        else if (!string.IsNullOrWhiteSpace(item.OriginalName) && !ContainsKorean(item.OriginalName))
            raw = item.OriginalName;

        else if (!string.IsNullOrWhiteSpace(item.NameRu))
        {
            var translit = new Transliterator(x => x
                .OnDuplicateToken(DuplicateTokenBehaviour.TakeFirst)
                .ConfigureCyrillicToLatin());

            raw = translit.Transliterate(item.NameRu);
        }

        else if (!string.IsNullOrWhiteSpace(item.NameUz))
        {
            var translit = new Transliterator(x => x
                .OnDuplicateToken(DuplicateTokenBehaviour.TakeFirst)
                .ConfigureCyrillicToLatin());

            raw = translit.Transliterate(item.NameUz);
        }

        if (string.IsNullOrWhiteSpace(raw))
            return Guid.NewGuid().ToString();

        return SanitizePermalink(ToKebabCase(raw));
    }

    private string SanitizePermalink(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        input = input.ToLowerInvariant();
        input = Regex.Replace(input, @"[^\w\-]", "-");
        input = Regex.Replace(input, @"-+", "-");
        return input.Trim('-');
    }



    private static string ToKebabCase(string input)
    {
        input = Regex.Replace(input, @"[\s\.,]+", "-");
        return input.ToLowerInvariant().Trim('-');
    }

    private static bool ContainsKorean(string input)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        var regex = new Regex(@"[\u1100-\u11FF\u3130-\u318F\uA960-\uA97F\uAC00-\uD7AF\uD7B0-\uD7FF]");
        return regex.IsMatch(input);
    }
}