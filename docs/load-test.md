# Load test

How the site holds up under load (T-0129): [k6](https://grafana.com/docs/k6/) on a GitHub runner against the test environment, started by hand. The script is `loadtest/site.js`; the workflow is **load-test**.

## Before you start

- Run it against test only. Test scales to zero when idle and to at most two replicas.
- For `search` and `scaleout`, raise test's search limit, or the limiter answers most searches with 429. The runner is a single address, and search allows 30 a minute per address. Add the setting to the repository variable, keeping what's there, then run `infra-apply` for test:

  ```sh
  gh variable get APP_SETTINGS_TEST > s.json
  jq -c '. + {"RateLimits__SearchPerMinute": "6000"}' s.json > s2.json
  gh variable set APP_SETTINGS_TEST < s2.json && rm s.json s2.json
  ```

  Afterwards, remove it the same way with `jq -c 'del(.RateLimits__SearchPerMinute)'`, and run `infra-apply` again.
- The sample site should be loaded (the **seed** workflow): the scenarios read its sitemap.

## Run it

Actions → **load-test** → Run workflow, then choose a scenario and a scale (1 by default, which multiplies the users and rates):

| Scenario | What it does | What it shows |
| --- | --- | --- |
| `smoke` | One user for 30 seconds, pages and searches | That the test and the site work. Run it first |
| `coldstart` | One request to the home page | How long the first page takes after the site has scaled to zero. Run it after at least 10 idle minutes |
| `pages` | Up to 10 readers for 4 minutes: home, blog, posts, tags, feeds and the sitemap, a second or two apart | The output cache: pages should stay fast and use little CPU |
| `search` | 2, then 5, then 10 searches a second, every one different, so none comes from the cache | The CPU limit: each search embeds its words, at 0.25 or 0.5 vCPU a replica |
| `scaleout` | Up to 40 users for 6 minutes, 7 in 10 requests pages and 3 in 10 searches | The second replica: Container Apps adds it at about 10 concurrent requests |

A run fails when more than 1 % of requests fail, when anything is rate-limited, or when p95 is over 1.5 s for pages, 2 s for feeds or 5 s for searches.

## Read the results

The run's summary shows:

- the requests, the failure rate and the rate-limited count;
- the median, p95, p99 and maximum time for each kind of request;
- for each minute, the web app's replicas, CPU (millicores) and memory, from Azure Monitor.

The k6 summary (`summary.json`) is kept as an artifact for 30 days. The site's address is masked in the log, and the results name kinds of request, not URLs.

For the server's own time per request, without the network, query Application Insights for the same minutes:

```kusto
requests
| where timestamp > ago(1h)
| summarize count(), p50 = percentile(duration, 50), p95 = percentile(duration, 95) by name, bin(timestamp, 1m)
```

## What it can and can't tell

- **GitHub's runners are usually in US regions**, and test is in Poland Central: every request carries about 100 to 150 ms of network. Compare the server's own times in Application Insights.
- **One address:** fine for pages, feeds and scale-out; search needs the raised limit above. It doesn't test the rate limits themselves.
- **No browser:** k6 fetches the HTML, not the CSS, fonts, scripts and images, which are static files that browsers cache. It asks for compressed responses as browsers do; without that, pages are about three times larger and take extra round trips from far away.
- **Not the admin or MCP:** both need a sign-in, and both are used by a few people at a time.
- **Cost:** a run costs cents: a few minutes of a second replica, and a little Application Insights data.
