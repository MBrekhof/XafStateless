# XafStateless

A proof of concept for **end-user-defined workflows in DevExpress XAF apps**: states, transitions and what each state
does are defined at runtime, as data, by the people who use the app. Built on the
[Stateless](https://github.com/dotnet-state-machine/stateless) state-machine library.

## Why

The XafElsa POC (MBrekhof/XafElsa, `docs/FINDINGS.md`) compared Elsa Workflows 3.8.4 with a plain baseline (a status
field, a Stateless transition table and a per-minute Hangfire job) on the app BPG generates. The baseline won every
measured comparison, and Elsa's editor turned out to be an IT tool. In the scenario that mattered, the important part
was one opaque node.

XafElsa's POC-009 made the baseline user-editable, but only partway: users can re-route edges between **fixed**
states. What a state does (create a task, wait, notify) was still code. This repo takes the next step: **new states,
defined by an end user, at runtime.**

## The idea

- **States are data**: rows, not an enum. Stateless builds the machine at runtime from a snapshot of those rows.
- **Behaviour comes from a small, fixed set of step types**, configured but not programmed by the user: a human task
  (role, allowed outcomes), a timer/wait (duration, then trigger X), a notification (template), set a field. A
  developer adds a new step type; users combine the existing ones freely.
- **Safe to edit**: versioned definitions (Draft → Published → Retired), running work stays on the version it started
  on, and validation at publish time (no unreachable state, no state without a way out, no stuck wait).
- **An editor an end user can manage**: add a state, draw an edge, pick "on entry: task for Office". The diagram is
  generated from the same definition that runs (Stateless exports Mermaid and DOT).

## Builds on (from XafElsa)

- POC-009: versioned `TransitionSet` / `TransitionRule` rows, the immutability interceptor, publish-time validation and
  the dry run, and records stamped with their version.
- The baseline pattern's shared pieces: `WorkflowTask` + decision service, the notification outbox + dispatcher, the
  lost-race retry helper, and the timer-job pattern. The adapter-shaped versions live in XafElsa `driver/`.

## Open questions (to settle before the plan)

1. Which step types must an end user be able to configure? Is the list above the right one?
2. Host app: the BPGDemo skeleton, as in XafElsa, or a smaller XAF app?
3. The editor: a diagram editor in the browser, or forms first with a generated diagram?

## Status

Empty: no code yet. Open work lives on ContextBoard, project **XafStateless**.
