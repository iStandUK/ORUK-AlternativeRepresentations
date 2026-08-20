# Design – ORUK MCP Server

This folder contains the conceptual design for an **MCP (Model Context Protocol) server** that exposes Open Referral UK service data to agentic AI systems such as Microsoft Copilot 365, ChatGPT (via GPT Actions / Custom Connectors) and Claude.

## Purpose

The documents here explore **what becomes possible** when an AI assistant can query a live, structured directory of local public and voluntary sector services in a conversational context.  They are deliberately non-technical: no code, no schemas.  The goal is to describe real human benefit.

## Contents

| File | Description |
|------|-------------|
| [mcp-server-concept.md](mcp-server-concept.md) | What the MCP server is, how it connects to ORUK feeds, and the full capability model |
| [taxonomies.md](taxonomies.md) | How ORUK taxonomies enable targeted API queries, and how the AI agent mediates between user language and taxonomy vocabulary |
| [oruk-api-client-design.md](oruk-api-client-design.md) | Design for the `OrukApiClient` library: why `IOrukFeedPageFetcher` must be replaced, the new `IOrukServiceClient` / `IOrukTaxonomyClient` interfaces, `OrukServiceQuery`, and impact on existing projects |
| [mcp-server-design.md](mcp-server-design.md) | C# implementation design: project structure, `ModelContextProtocol` SDK, tool classes, taxonomy cache, stdio transport, development workflow, and path to HTTP deployment |
| [personas.md](personas.md) | Four representative UK residents who would benefit, with demographic context |
| [use-cases.md](use-cases.md) | Concrete use cases derived from each persona, with example AI conversations |
| [vector-search-explained.md](vector-search-explained.md) | Non-technical explanation of embeddings and vector databases, using real yoga classes from the feeds |

## Reading Order

1. [mcp-server-concept.md](mcp-server-concept.md) — capability overview and tool surface
2. [taxonomies.md](taxonomies.md) — how the AI bridges natural language and structured classification
3. [oruk-api-client-design.md](oruk-api-client-design.md) — ORUK query client library design (prerequisite for MCP server)
4. [mcp-server-design.md](mcp-server-design.md) — C# technical design for the MCP server host
5. [personas.md](personas.md) — who benefits and why
6. [use-cases.md](use-cases.md) — what it looks like in practice
7. [vector-search-explained.md](vector-search-explained.md) — background: how meaning-based search would differ from word matching
