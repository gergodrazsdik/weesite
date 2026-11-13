// A load test for a Weesite site (T-0129), run with k6 by the load-test workflow against test
// (docs/load-test.md). One scenario per run, from SCENARIO:
//
// - smoke: one user for 30 seconds, pages and searches: that the test and the site work.
// - coldstart: one request after the site has scaled to zero; how long the first page takes.
// - pages: readers on cached pages (home, blog, posts, tags, feeds, sitemap): the output cache.
// - search: searches that are all different, so none is cached: the embedding model, the CPU.
// - scaleout: pages and searches together, with enough at once for a second replica.
//
// SITE_URL is the site's address; SCALE (1 by default) multiplies the users and rates. Requests
// are tagged by kind (name), so the results hold no addresses.
import http from "k6/http";
import { check, sleep } from "k6";
import { Counter, Trend } from "k6/metrics";

const BASE = (__ENV.SITE_URL ?? "").replace(/\/$/, "");
const SCENARIO = __ENV.SCENARIO ?? "pages";
const SCALE = Math.max(1, Number(__ENV.SCALE ?? "1"));

const firstPage = new Trend("first_page", true);
const rateLimited = new Counter("rate_limited");

const scenarios = {
  smoke: {
    executor: "constant-vus",
    exec: "mixed",
    vus: 1,
    duration: "30s",
  },
  coldstart: {
    executor: "per-vu-iterations",
    exec: "coldStart",
    vus: 1,
    iterations: 1,
    maxDuration: "3m",
  },
  pages: {
    executor: "ramping-vus",
    exec: "pages",
    startVUs: 1,
    stages: [
      { duration: "1m", target: 10 * SCALE },
      { duration: "3m", target: 10 * SCALE },
      { duration: "30s", target: 0 },
    ],
  },
  search: {
    executor: "ramping-arrival-rate",
    exec: "search",
    startRate: 1,
    timeUnit: "1s",
    preAllocatedVUs: 20 * SCALE,
    maxVUs: 100 * SCALE,
    stages: [
      { duration: "1m", target: 2 * SCALE },
      { duration: "2m", target: 5 * SCALE },
      { duration: "2m", target: 10 * SCALE },
      { duration: "30s", target: 0 },
    ],
  },
  scaleout: {
    executor: "ramping-vus",
    exec: "mixed",
    startVUs: 1,
    stages: [
      { duration: "1m", target: 20 * SCALE },
      { duration: "5m", target: 40 * SCALE },
      { duration: "30s", target: 0 },
    ],
  },
};

if (!scenarios[SCENARIO]) {
  throw new Error(`SCENARIO must be one of ${Object.keys(scenarios).join(", ")}; got ${SCENARIO}`);
}

export const options = {
  scenarios: { [SCENARIO]: scenarios[SCENARIO] },
  discardResponseBodies: true,
  // Failures fail the run; the latency limits allow for a runner in another region.
  thresholds: {
    http_req_failed: ["rate<0.01"],
    "http_req_duration{name:page}": ["p(95)<1500"],
    "http_req_duration{name:feed}": ["p(95)<2000"],
    "http_req_duration{name:search}": ["p(95)<5000"],
    rate_limited: ["count<1"],
  },
  summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

// The site's pages and posts, from its sitemap, as paths.
export function setup() {
  if (!BASE) {
    throw new Error("SITE_URL is missing.");
  }

  // The cold start's first request must be the one it measures.
  if (SCENARIO === "coldstart") {
    return { paths: [] };
  }

  const sitemap = http.get(`${BASE}/sitemap.xml`, { responseType: "text", tags: { name: "sitemap" }, timeout: "120s" });
  const paths = [...(sitemap.body ?? "").matchAll(/<loc>([^<]+)<\/loc>/g)]
    .map((m) => m[1].replace(/^https?:\/\/[^/]+/, "") || "/")
    .filter((p) => p !== "/");
  if (paths.length === 0) {
    throw new Error("The sitemap has no pages: is the sample site loaded?");
  }

  return { paths };
}

function get(path, name) {
  const response = http.get(`${BASE}${path}`, { tags: { name }, timeout: "60s" });
  if (response.status === 429) {
    rateLimited.add(1);
  }
  check(response, { [`${name} is 200`]: (r) => r.status === 200 });
  return response;
}

const pick = (list) => list[Math.floor(Math.random() * list.length)];

function readOne(data) {
  const roll = Math.random();
  if (roll < 0.25) {
    get("/", "page");
  } else if (roll < 0.35) {
    get(pick(["/feed.xml", "/feed.json"]), "feed");
  } else if (roll < 0.4) {
    get("/sitemap.xml", "sitemap");
  } else {
    get(pick(data.paths), "page");
  }
}

// Words from the sample site, two or three at a time and in any order, plus a number: every
// query is new, so the search cache never answers and each one embeds.
const WORDS = [
  "harbour", "sunset", "almond", "garden", "abbey", "island", "snow", "city", "night", "boat",
  "ruins", "tests", "search", "deploy", "coffee", "keyboard", "forms", "theme", "config", "azure",
  "bridge", "lighthouse", "forest", "walk", "photo", "history", "street", "museum", "palace", "tree",
];

function query() {
  const count = 2 + Math.floor(Math.random() * 2);
  const words = Array.from({ length: count }, () => pick(WORDS));
  return `${words.join(" ")} ${Math.floor(Math.random() * 1_000_000)}`;
}

export function coldStart() {
  const started = Date.now();
  const response = get("/", "page");
  firstPage.add(Date.now() - started);
  check(response, { "first page within 60 s": (r) => r.timings.duration < 60_000 });
}

export function pages(data) {
  readOne(data);
  sleep(1 + Math.random());
}

export function search() {
  get(`/search?q=${encodeURIComponent(query())}`, "search");
}

export function mixed(data) {
  if (Math.random() < 0.3) {
    search();
  } else {
    readOne(data);
  }
  sleep(0.5 + Math.random());
}
