# Holdout set

Emails here are used **only** to measure accuracy (`Holdout_accuracy_report` in `Sift.Tests`).
Don't look at them while tuning parsers, or the numbers stop meaning anything.

Each sample is a pair:

- `name.eml`: a synthetic email (fictional merchants, `.example` domains, no real personal data)
- `name.expected.json`: what a perfect parser would extract (same format as `samples/dev`).
  Use `null` for a field that the email doesn't contain or that is genuinely ambiguous.

Ideally someone other than the parser's author writes these, using templates and styles that
differ from the dev set.
