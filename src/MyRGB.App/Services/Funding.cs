namespace MyRGB.App.Services;

/// <summary>
/// Where people can support MyRGB. A link that's null isn't shown, so the app never points at a
/// page that doesn't exist. Ko-fi payments make the payer a supporter automatically (see
/// supabase/functions/kofi-webhook); GitHub Sponsors are added by hand for now.
/// </summary>
public static class Funding
{
    public static string? KoFiUrl => null;
    public static string? GitHubSponsorsUrl => null;

    public static IReadOnlyList<(string Name, string Url)> Links =>
        new[] { ("Ko-fi", KoFiUrl), ("GitHub Sponsors", GitHubSponsorsUrl) }
            .Where(l => l.Item2 is not null)
            .Select(l => (l.Item1, l.Item2!))
            .ToList();
}
