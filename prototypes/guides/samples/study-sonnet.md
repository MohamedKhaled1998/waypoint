# study (sonnet)

## First Guides

## SQL practice on the laptop (30 min, 3× a week)

1. **Warm up (5 min).** Re-run the last query you wrote. Say out loud what it does in Excel terms.
2. **Do the puzzles (20 min).** Use a free in-browser SQL practice site, so nothing needs installing. Pick Postgres-style exercises if there's a choice. Aim for about 3-6 exercises, since that's enough to learn from without rushing, on a tired evening.
3. **Save one query (5 min).** Paste your best query into a notes file with a one-line comment on what question it answers.

Work in this order, moving on when the current step feels easy:
- **Weeks 1-2:** SELECT, WHERE, ORDER BY. This is like filtering and sorting a sheet.
- **Weeks 3-4:** GROUP BY with COUNT and SUM. This is your pivot table.
- **Weeks 5-6:** JOIN. This is VLOOKUP, but it can match many rows.

If you miss a session, just do the next one. Nothing needs catching up.

## Train reading: one SQL idea (15 min, 4× a week)

1. **Read one short article or lesson (about 5-8 min)** on a single idea. Pick text over video, since you said long videos lose you. Stop after one idea.
2. **Make it a puzzle (about 5 min).** In your phone's notes, write a query for a real sales question, such as "total sales per region". Don't worry about running it.
3. **Save it for home.** Bring that query to your next laptop session and run it there.

About 1 idea per ride is a good starting point because small chunks stick better, and you're reading on a phone. Follow the same order as the laptop sessions: filtering, then grouping, then joins.

```json
{
  "guides": [
    {
      "action": "SQL practice on the laptop",
      "body": "1. **Warm up (5 min).** Re-run the last query you wrote. Say out loud what it does in Excel terms.\n2. **Do the puzzles (20 min).** Use a free in-browser SQL practice site, so nothing needs installing. Pick Postgres-style exercises if there's a choice. Aim for about 3-6 exercises, since that's enough to learn from without rushing, on a tired evening.\n3. **Save one query (5 min).** Paste your best query into a notes file with a one-line comment on what question it answers.\n\nWork in this order, moving on when the current step feels easy:\n- **Weeks 1-2:** SELECT, WHERE, ORDER BY. This is like filtering and sorting a sheet.\n- **Weeks 3-4:** GROUP BY with COUNT and SUM. This is your pivot table.\n- **Weeks 5-6:** JOIN. This is VLOOKUP, but it can match many rows.\n\nIf you miss a session, just do the next one. Nothing needs catching up.",
      "targets": [
        {
          "what": "Practice exercises per session",
          "low": 3,
          "high": 6,
          "unit": "exercises",
          "why": "Enough to learn from without rushing on a tired evening."
        }
      ],
      "withheld": [],
      "referral": ""
    },
    {
      "action": "Train reading: one SQL idea",
      "body": "1. **Read one short article or lesson (about 5-8 min)** on a single idea. Pick text over video, since you said long videos lose you. Stop after one idea.\n2. **Make it a puzzle (about 5 min).** In your phone's notes, write a query for a real sales question, such as \"total sales per region\". Don't worry about running it.\n3. **Save it for home.** Bring that query to your next laptop session and run it there.\n\nAbout 1 idea per ride is a good starting point because small chunks stick better, and you're reading on a phone. Follow the same order as the laptop sessions: filtering, then grouping, then joins.",
      "targets": [
        {
          "what": "Reading per ride",
          "low": 5,
          "high": 8,
          "unit": "minutes",
          "why": "Short enough to finish on a phone and leave time for the puzzle."
        },
        {
          "what": "New ideas per ride",
          "low": 1,
          "high": 1,
          "unit": "idea",
          "why": "Small chunks stick better and fit a 15-minute slot."
        }
      ],
      "withheld": [],
      "referral": ""
    }
  ]
}
```

## Checks

  ok  "SQL practice on the laptop": 152 words (guide 60–180)
  ok    Practice exercises per session: 3–6 exercises is a range
  !!    mentions "install" (a dislike or limit)
  ok  "Train reading: one SQL idea": 111 words (guide 60–180)
  ok    Reading per ride: 5–8 minutes is a range
  !!    New ideas per ride: 1–1 idea is a range

## Weekly refresh

Here's what I changed:

- **Practice, joins:** You found joins clicked once you drew the tables, so drawing the tables is now part of the warm-up, and you can start joins whenever grouping feels okay instead of waiting for weeks 5-6.
- **Practice, tired-day option:** Added a 10-minute version for evenings like Wednesday, so a tired night becomes a short session instead of a skipped one.
- **Practice, numbers:** Kept 3-6 exercises. You did 2 of 3 sessions, so I'm holding the load this week.
- **Train reading, recall:** You forgot most of it by the evening, so there's a new step: write one line from memory on your phone at the end of the ride, then try it again when you sit down at home. Recalling something helps it stick more than rereading it.
- **Train reading, volume:** No change to length or number of ideas. Four of four done is good, and the issue is remembering, not amount.

## SQL practice on the laptop (30 min, 3× a week)

1. **Warm up (5 min).** Sketch the tables you're about to use: boxes with their column names, and a line where they connect. Then re-run your last saved query.
2. **Do the puzzles (20 min).** Use a free in-browser SQL practice site, so nothing needs installing. Aim for about 3-6 exercises, which is enough to learn from without rushing on a tired evening.
3. **Save one query (5 min).** Paste your best one into your notes with a one-line comment on the question it answers.

**Tired night?** Do 10 minutes: sketch the tables, run one saved query, change one thing about it. That counts.

Order: filtering and sorting, then GROUP BY (your pivot table), then JOIN (VLOOKUP that can match many rows). Start joins whenever grouping feels okay.

## Train reading: one SQL idea (15 min, 4× a week)

1. **Read one short article or lesson (about 5-8 min)** on a single idea. Text over video. Stop after one idea.
2. **Write a query on your phone (about 4 min)** for a real sales question, such as "total sales per region". It doesn't need to run.
3. **Recall it (about 2 min).** Close the article and type one line from memory: what the idea does and when you'd use it.
4. **Test it at home.** At the start of your evening session, try to write that query again without looking, then check it against your note.

About 1 idea per ride is a good starting point because small chunks stick better on a phone.

```json
{
  "guides": [
    {
      "action": "SQL practice on the laptop",
      "body": "1. **Warm up (5 min).** Sketch the tables you're about to use: boxes with their column names, and a line where they connect. Then re-run your last saved query.\n2. **Do the puzzles (20 min).** Use a free in-browser SQL practice site, so nothing needs installing. Aim for about 3-6 exercises, which is enough to learn from without rushing on a tired evening.\n3. **Save one query (5 min).** Paste your best one into your notes with a one-line comment on the question it answers.\n\n**Tired night?** Do 10 minutes: sketch the tables, run one saved query, change one thing about it. That counts.\n\nOrder: filtering and sorting, then GROUP BY (your pivot table), then JOIN (VLOOKUP that can match many rows). Start joins whenever grouping feels okay.",
      "targets": [
        {
          "what": "Practice exercises per session",
          "low": 3,
          "high": 6,
          "unit": "exercises",
          "why": "Enough to learn from without rushing on a tired evening; held steady this week because one session was skipped."
        },
        {
          "what": "Short tired-day session",
          "low": 10,
          "high": 10,
          "unit": "minutes",
          "why": "A small version keeps the habit going on low-energy nights."
        }
      ],
      "withheld": [],
      "referral": ""
    },
    {
      "action": "Train reading: one SQL idea",
      "body": "1. **Read one short article or lesson (about 5-8 min)** on a single idea. Text over video. Stop after one idea.\n2. **Write a query on your phone (about 4 min)** for a real sales question, such as \"total sales per region\". It doesn't need to run.\n3. **Recall it (about 2 min).** Close the article and type one line from memory: what the idea does and when you'd use it.\n4. **Test it at home.** At the start of your evening session, try to write that query again without looking, then check it against your note.\n\nAbout 1 idea per ride is a good starting point because small chunks stick better on a phone.",
      "targets": [
        {
          "what": "Reading per ride",
          "low": 5,
          "high": 8,
          "unit": "minutes",
          "why": "Short enough to finish on a phone and leave time to write and recall."
        },
        {
          "what": "New ideas per ride",
          "low": 1,
          "high": 1,
          "unit": "idea",
          "why": "Small chunks stick better, and recall works best on one thing at a time."
        }
      ],
      "withheld": [],
      "referral": ""
    }
  ]
}
```

## Checks

  ok  "SQL practice on the laptop": 128 words (guide 60–180)
  ok    Practice exercises per session: 3–6 exercises is a range
  !!    Short tired-day session: 10–10 minutes is a range
  !!    mentions "install" (a dislike or limit)
  ok  "Train reading: one SQL idea": 115 words (guide 60–180)
  ok    Reading per ride: 5–8 minutes is a range
  !!    New ideas per ride: 1–1 idea is a range
