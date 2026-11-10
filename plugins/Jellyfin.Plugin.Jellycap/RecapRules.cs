using Jellyfin.Plugin.Jellycap.Configuration;

namespace Jellyfin.Plugin.Jellycap;

/// <summary>Decides whether an episode gets a recap and in which format.</summary>
public static class RecapRules
{
    public readonly record struct Decision(bool Allowed, RecapFormat Format, ResumeContent ResumeContent)
    {
        /// <summary>Mode for a resumed episode, from what its recap should cover.</summary>
        public RecapMode ResumeMode => ResumeContent switch
        {
            ResumeContent.PreviousEpisodes => RecapMode.New,
            ResumeContent.ThisEpisode => RecapMode.ResumeThisEpisode,
            _ => RecapMode.Resume
        };
    }

    public static Decision Decide(Guid seriesId, int? season, int? episode, PluginConfiguration cfg)
    {
        var rule = seriesId == Guid.Empty
            ? null
            : cfg.SeriesRules?.FirstOrDefault(r => Guid.TryParse(r.SeriesId, out var g) && g == seriesId);

        if (rule is null)
        {
            return new Decision(!cfg.OnlyListedSeries, cfg.RecapFormat, cfg.ResumeRecapContent);
        }

        var format = rule.Format switch
        {
            RuleFormat.Video => RecapFormat.Video,
            RuleFormat.Text => RecapFormat.Text,
            RuleFormat.Both => RecapFormat.Both,
            _ => cfg.RecapFormat
        };

        var content = rule.ResumeContent switch
        {
            RuleResumeContent.Both => ResumeContent.Both,
            RuleResumeContent.PreviousEpisodes => ResumeContent.PreviousEpisodes,
            RuleResumeContent.ThisEpisode => ResumeContent.ThisEpisode,
            _ => cfg.ResumeRecapContent
        };

        var allowed = rule.Enabled && InSpec(rule.Seasons, season) && InSpec(rule.Episodes, episode);
        return new Decision(allowed, format, content);
    }

    /// <summary>"1,3-5,8-" style list. Empty/blank = everything. Unparsable tokens are ignored.</summary>
    public static bool InSpec(string? spec, int? value)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return true;
        }

        if (value is null)
        {
            return false;
        }

        foreach (var token in spec.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(token, out var n) && n == value)
                {
                    return true;
                }

                continue;
            }

            var left = token[..dash];
            var right = token[(dash + 1)..];
            var lo = left.Length == 0 ? int.MinValue : (int.TryParse(left, out var l) ? l : int.MaxValue);
            var hi = right.Length == 0 ? int.MaxValue : (int.TryParse(right, out var r) ? r : int.MinValue);
            if (value >= lo && value <= hi)
            {
                return true;
            }
        }

        return false;
    }
}
