using System.Text.Json;
using Modelry.Core.Audit;
using Xunit;

namespace Modelry.Web.Tests;

public class AuditSummaryTests
{
    private static AuditSummarySettings Settings(string language = "") => new()
    {
        MaxWords = 100, TargetLanguage = language, PromptTemplate = "Data:\n{{auditData}}\nMax {{maxWords}} words."
    };

    /// <summary>A small Tridion audit with names that must never reach the AI service.</summary>
    private static AuditSnapshot Snapshot()
    {
        var s = new AuditSnapshot { PublicationId = "tcm:0-42-1", PublicationTitle = "SECRET Publication", CmsHost = "cms.secret-host.example", RunBy = "DOMAIN\\secret.user" };
        s.Items[AuditKind.Pages].Add(new AuditRow
        {
            Id = "tcm:42-100-64", Title = "Copy of SECRET page", Kind = AuditKind.Pages, Path = "SECRET/Folder", ModifiedBy = "DOMAIN\\secret.user",
            Publish = PublishState.Unpublished, Values = { ["fileName"] = "Secret Page", ["cps"] = "0" }
        });
        s.Items[AuditKind.Pages].Add(new AuditRow
        {
            Id = "tcm:42-101-64", Title = "Home", Kind = AuditKind.Pages, Path = "", Publish = PublishState.Published, Values = { ["fileName"] = "index", ["cps"] = "3" }
        });
        AuditFinalizer.Finalize(s);
        return s;
    }

    [Fact]
    public void Prompt_data_holds_scores_counts_and_check_names_but_no_names_paths_ids_or_users()
    {
        var s = Snapshot();
        var data = AuditSummaryPrompt.BuildData(s, Settings(), new AuditSummaryPrompt.Previous(new DateTime(2026, 9, 1), 61, 12));

        Assert.Contains($"Health score: {s.Health!.Overall}/100", data);
        Assert.Contains("Pages 2 (1 live, 1 not live)", data);
        Assert.Contains("Suspicious names", data);          // a check name
        Assert.Contains("Previous audit 2026-09-01: score 61/100", data);
        foreach (var secret in new[] { "SECRET", "secret", "tcm:", "index", "Home" })
            Assert.DoesNotContain(secret, data);
    }

    [Fact]
    public void Prompt_template_placeholders_are_filled()
    {
        var text = AuditSummaryPrompt.Fill(Settings().PromptTemplate, "DATA", 100);
        Assert.Equal("Data:\nDATA\nMax 100 words.", text);
    }

    [Fact]
    public void Structured_answer_is_parsed_also_inside_a_json_fence()
    {
        var json = """
            ```json
            { "headline": "Site is in good shape", "status": "Healthy", "summary": "Most content is live and current.",
              "actions": [ { "priority": "Now", "action": "Fix two broken page addresses" } ], "decision": "Approve a small clean-up." }
            ```
            """;
        var s = AuditAiSummary.From(json, null, null, 100);

        Assert.Equal("Site is in good shape", s.English!.Headline);
        Assert.Equal("Now", s.English.Actions.Single().Priority);
        Assert.False(s.Trimmed);
        Assert.False(s.HasTranslation);
    }

    [Fact]
    public void Plain_text_answer_is_kept_as_text()
    {
        var s = AuditAiSummary.From("The website is healthy overall.", null, null, 100);
        Assert.Null(s.English);
        Assert.Equal("The website is healthy overall.", s.EnglishText);
    }

    [Fact]
    public void Answer_longer_than_the_limit_is_trimmed_to_it()
    {
        var longText = string.Join(' ', Enumerable.Repeat("word", 60));
        var e = new ExecutiveSummary
        {
            Headline = "Headline of six words here", Status = "At risk", Summary = longText, Decision = longText,
            Actions = { new() { Priority = "Now", Action = longText }, new() { Priority = "Next", Action = "Do this" }, new() { Priority = "Later", Action = "Do that" } }
        };
        var s = AuditAiSummary.From(JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web)), null, null, 100);

        Assert.True(s.Trimmed);
        Assert.True(s.English!.Words <= 100, $"{s.English.Words} words");
        Assert.Equal(s.English.Words, s.Words);
        Assert.NotEmpty(s.English.Actions);
    }

    [Fact]
    public void Plain_text_longer_than_the_limit_is_cut_with_an_ellipsis()
    {
        var s = AuditAiSummary.From(string.Join(' ', Enumerable.Repeat("word", 150)), null, null, 100);
        Assert.True(s.Trimmed);
        Assert.Equal(100, AuditAiSummary.CountWords(s.EnglishText));
        Assert.EndsWith("…", s.EnglishText);
    }

    [Fact]
    public void Translation_tab_only_when_a_language_is_configured()
    {
        const string en = """{ "headline": "Good", "status": "Healthy", "summary": "Fine.", "actions": [], "decision": "None." }""";
        const string ar = """{ "headline": "جيد", "status": "Healthy", "summary": "جيد.", "actions": [], "decision": "لا شيء." }""";

        var without = AuditAiSummary.From(en, ar, null, 100);
        var with = AuditAiSummary.From(en, ar, "ar", 100);

        Assert.False(without.HasTranslation);
        Assert.True(with.HasTranslation);
        Assert.Equal("جيد", with.Translated!.Headline);
        Assert.Equal("Good", with.English!.Headline);   // English always
    }

    [Fact]
    public void Request_language_is_the_target_or_the_no_translation_language()
    {
        Assert.Equal("ar", Settings("ar").RequestLanguage);
        Assert.Equal("en", Settings().RequestLanguage);
        Assert.Null(new AuditSummarySettings { NoTranslationLanguage = "", PromptTemplate = "{{auditData}}" }.RequestLanguage);
    }

    [Fact]
    public void Settings_validation_needs_the_audit_data_placeholder()
    {
        Assert.Contains(new AuditSummarySettings { PromptTemplate = "no placeholder" }.Validate(), e => e.Contains("{{auditData}}"));
        Assert.Empty(Settings().Validate());
    }

    [Fact]
    public void Schema_object_in_the_settings_is_sent_as_compact_json_text()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AuditSummarySettings>("""{ "PromptTemplate": "{{auditData}}", "ResponseSchema": { "type": "object", "properties": { "headline": { "type": "string" } } } }""")!;
        Assert.Equal("""{"type":"object","properties":{"headline":{"type":"string"}}}""", s.ResponseSchemaText);
        Assert.Empty(s.Validate());

        var asText = System.Text.Json.JsonSerializer.Deserialize<AuditSummarySettings>("""{ "PromptTemplate": "{{auditData}}", "ResponseSchema": "{ headline, status }" }""")!;
        Assert.Equal("{ headline, status }", asText.ResponseSchemaText);
        Assert.Empty(asText.Validate());

        Assert.Null(new AuditSummarySettings().ResponseSchemaText);
    }

    [Fact]
    public void Empty_answer_is_an_error()
    {
        Assert.Throws<InvalidDataException>(() => AuditAiSummary.From("  ", null, null, 100));
    }
}
