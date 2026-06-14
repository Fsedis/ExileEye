namespace ExileEye.Core;

/// <summary>
/// Sliding-window throttle for the trade API, configured from its X-Rate-Limit headers (the
/// limits are per-IP and shared across every tool hitting the API, so they're easy to trip).
/// Ported in spirit from Exiled Exchange 2's RateLimiter: before each request we wait until a
/// slot is free in every rule, so requests slow down instead of failing with 429.
/// </summary>
public sealed class RateLimiter
{
    private sealed record Rule(int Max, int WindowSec)
    {
        public readonly Queue<long> Hits = new();
    }

    private readonly object _lock = new();
    // A conservative default until the first response tells us the real limits.
    private List<Rule> _rules = [new Rule(6, 10)];

    /// <summary>Update the rules from an "X-Rate-Limit-Ip" header value ("8:10:60,15:60:120,...").</summary>
    public void Configure(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return;
        var rules = new List<Rule>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var nums = part.Split(':');
            if (nums.Length >= 2 && int.TryParse(nums[0], out var max) && int.TryParse(nums[1], out var win) && max > 0 && win > 0)
                rules.Add(new Rule(max, win));
        }
        if (rules.Count == 0) return;
        lock (_lock)
        {
            // Preserve recent hit timestamps when the rule set is unchanged in shape.
            if (rules.Count == _rules.Count)
                for (int i = 0; i < rules.Count; i++)
                    foreach (var t in _rules[i].Hits) rules[i].Hits.Enqueue(t);
            _rules = rules;
        }
    }

    /// <summary>Block until a request can be made under every rule, then record it.</summary>
    public async Task AcquireAsync()
    {
        while (true)
        {
            long now = Environment.TickCount64;
            long wait = 0;
            lock (_lock)
            {
                foreach (var r in _rules)
                {
                    while (r.Hits.Count > 0 && now - r.Hits.Peek() >= r.WindowSec * 1000L) r.Hits.Dequeue();
                    if (r.Hits.Count >= r.Max)
                        wait = Math.Max(wait, r.Hits.Peek() + r.WindowSec * 1000L - now);
                }
                if (wait <= 0)
                {
                    foreach (var r in _rules) r.Hits.Enqueue(now);
                    return;
                }
            }
            await Task.Delay((int)wait + 25);
        }
    }
}
