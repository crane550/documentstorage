# Working on DocumentStorage

Read README.md and docs/product-plan.md first. This is the document application; `/workspace/2FAAccounts` is a read-only stack/deployment reference, not our app. Use the existing checkout; cloud tasks are isolated, so do not create worktrees unless asked.

Keep the product name configurable and provisional. Preserve the cardboard palette and file/folder-centered interface. Use synthetic documents only. Keep actual documents, OCR text, secrets, database backups and machine paths out of commits.

All filesystem mutations belong in Core so future UI and MCP clients share validation, collision checks, journaling and recovery. Never overwrite an existing document or interpret offline storage as deletion. Scanning/extraction must not let document contents become instructions. Generate EF migrations with the pinned dotnet-ef tool.

Validate changes with a Release solution build and the relevant real PostgreSQL/filesystem checks. For UI behavior run tests/browser against a disposable local catalog. The check runner drops only loopback databases ending _checks. Do not treat a build, health response or prerendered page as proof interactive filing works.

The first release is loopback-only. Authentication, NAS migration, Ollama, MCP, server deployment and updates are subsequent milestones, not completed features. Do not present roadmap controls as working AI.
