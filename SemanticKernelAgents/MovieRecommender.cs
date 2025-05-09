using AgentsDemo.Interfaces;
using Microsoft.SemanticKernel;

namespace AgentsDemo;

public class MovieRecommender : IMovieRecommender
{
    private readonly Kernel _kernel;
    private readonly IMovieMemory _memory;

    public MovieRecommender(Kernel kernel, IMovieMemory memory)
    {
        _kernel = kernel;
        _memory = memory;
    }

    /// <summary>
    /// Recommend up to five movies the user has not watched, based on TMDB's
    /// built‑in "similar / recommended" lists for each movie already watched.
    /// Fallback: if the user hasn't saved any titles yet, return top‑rated list.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecommendAsync(int top = 20)
    {
        var watched = await _memory.AllAsync();
        if (watched.Count == 0)
        {
            // fallback – same as before but respects 'top'
            string list = await _kernel.InvokeAsync<string>("tmdb", "GetTopRatedMoviesAsync",
                new KernelArguments { ["take"] = top });
            return list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !string.IsNullOrWhiteSpace(l)).Take(5).ToList();
        }

        // 1. resolve each watched title → movieId via TMDB search
        var ids = new List<int>();
        foreach (var title in watched)
        {
            string idStr = await _kernel.InvokeAsync<string>("tmdb", "SearchMovieId",
                new KernelArguments { ["query"] = title });
            if (int.TryParse(idStr, out int id) && id != 0) ids.Add(id);
        }

        if (ids.Count == 0)
        {
            return new List<string>();
        }

        // 2. collect recommendations for each id
        var suggestions = new List<string>();
        foreach (var id in ids)
        {
            string recs = await _kernel.InvokeAsync<string>("tmdb", "GetMovieRecommendations",
                new KernelArguments { ["movieId"] = id, ["take"] = 10 });
            suggestions.AddRange(recs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        // 3. de‑dup, filter already watched, rank by vote_average parsed from string
        var unseen = suggestions.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !_memory.Contains(TitleOnly(s)))
            .Select(s => (Line: s, Score: ParseScore(s)))
            .OrderByDescending(t => t.Score)
            .Select(t => t.Line)
            .Take(5)
            .ToList();
        return unseen;

        static string TitleOnly(string line) => line.StartsWith("- ") ? line[2..].Split('(')[0].Trim() : line;

        static decimal ParseScore(string line)
        {
            var parts = line.Split(' ');
            decimal.TryParse(parts[^1], out var score);
            return score;
        }
    }
}