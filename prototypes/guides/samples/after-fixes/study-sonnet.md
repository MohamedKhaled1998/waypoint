# study (sonnet)

## First Guides

```json
{
  "guides": [
    {
      "action": "SQL practice on the laptop",
      "body": "Use a free SQL practice page in your browser, so there's nothing to install and work can't block it. Pick one with sample sales-style tables. Postgres is likely what your team uses, so choose Postgres if offered.\n\n**Each session (30 min):**\n1. **Warm-up (5 min):** Re-run the last query you wrote. Change one thing and see what happens.\n2. **One real question (20 min):** Pick a question your team might ask, like \"total sales by region last month\". Build it in small pieces: `SELECT` and `FROM` first, then `WHERE`, then `GROUP BY`.\n3. **Save it (5 min):** Paste the working query into a note, with the question as a title.\n\n**Order to follow:** `SELECT`/`WHERE`/`ORDER BY`, then `GROUP BY` (your pivot table), then `JOIN` (your VLOOKUP).\n\nStuck for 10 minutes? Peek at the answer, then retype it yourself. Missing a session is fine. Just do the next one.",
      "targets": [],
      "withheld": [],
      "referral": ""
    },
    {
      "action": "Train reading: one SQL idea",
      "body": "One idea per ride, read on your phone. No videos, no theory first.\n\n**Each ride (15 min):**\n1. **Read one short example (5 min):** Look at a small query and its result table. Search for a short written tutorial on the idea, or open your saved notes.\n2. **Predict (5 min):** Before reading the answer, guess what the query returns. Then check.\n3. **Tweak it in your head (3 min):** What changes if you add a `WHERE` or swap the column?\n4. **Note one question (2 min):** Write down one work question to try on the laptop later.\n\n**Idea order:** `SELECT` and `WHERE`, `ORDER BY` and `LIMIT`, `COUNT` and `SUM`, `GROUP BY`, then `JOIN`.\n\nIf the train is packed or you're tired, reading one example is enough.",
      "targets": [],
      "withheld": [],
      "referral": ""
    }
  ]
}
```

## Checks

  ok  "SQL practice on the laptop": 147 words (guide 60–180)
  ok  "Train reading: one SQL idea": 127 words (guide 60–180)

## Weekly refresh

**Here's what I changed**

- **Practice, drawing step:** Added a quick sketch of the tables before you write a join, since that's what made joins click.
- **Practice, next step up:** You did two of three sessions, so I moved you one step on, from single joins to a join plus `GROUP BY` in one question.
- **Practice, short version:** Added a 10-minute option for tired evenings, so a low-energy day still counts.
- **Train reading, recall first:** Each ride now starts by recalling last time's idea from memory, because you forgot most of it by the evening.
- **Train reading, evening link:** The question you note on the train becomes your first task in the next laptop session, so the reading gets used within a day or two.

```json
{
  "guides": [
    {
      "action": "SQL practice on the laptop",
      "body": "Same setup: a free in-browser SQL practice page, Postgres if offered.\n\n**Each session (30 min):**\n1. **Warm-up (5 min):** Start with the question you noted on the train, or re-run your last saved query and change one thing.\n2. **Sketch first (3 min):** Draw the tables as boxes, with the column that links them. Joins clicked for you this way.\n3. **One real question (17 min):** This week, try questions that need a `JOIN` and a `GROUP BY` together, like \"total sales per customer region\". Build it in pieces: `SELECT`/`FROM`, then `JOIN`, then `GROUP BY`.\n4. **Save it (5 min):** Paste the working query into your notes, with the question as a title.\n\n**Tired evening?** Do the 10-minute version: re-run one saved query and change one thing.\n\nStuck for 10 minutes? Peek at the answer, then retype it yourself.",
      "targets": [],
      "withheld": [],
      "referral": ""
    },
    {
      "action": "Train reading: one SQL idea",
      "body": "One idea per ride, read on your phone. No videos.\n\n**Each ride (15 min):**\n1. **Recall (3 min):** Before opening anything, write or say what you remember of last ride's idea. Then check your notes.\n2. **Read one short example (5 min):** A small query and its result table.\n3. **Predict (4 min):** Guess what the query returns before reading the answer.\n4. **Note one question (3 min):** Write one work question using this idea. It's your warm-up at the next laptop session.\n\n**Idea order from here:** `GROUP BY`, then `JOIN` types (`INNER` and `LEFT`), then `HAVING`, then dates.\n\nIf you're tired, the recall step alone still helps.",
      "targets": [],
      "withheld": [],
      "referral": ""
    }
  ]
}
```

## Checks

  ok  "SQL practice on the laptop": 139 words (guide 60–180)
  ok  "Train reading: one SQL idea": 108 words (guide 60–180)
