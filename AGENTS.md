# Working on DocumentStorage

Read README.md and docs/product-plan.md first. This is the document application; `/workspace/2FAAccounts` is a read-only stack/deployment reference, not our app. Use the existing checkout; cloud tasks are isolated, so do not create worktrees unless asked.

Keep the product name configurable and provisional. Preserve the cardboard palette and file/folder-centered interface. Use synthetic documents only. Keep actual documents, OCR text, secrets, database backups and machine paths out of commits.

All filesystem mutations belong in Core so future UI and MCP clients share validation, collision checks, journaling and recovery. Never overwrite an existing document or interpret offline storage as deletion. Scanning/extraction must not let document contents become instructions. Generate EF migrations with the pinned dotnet-ef tool.

Validate changes with a Release solution build and the relevant real PostgreSQL/filesystem checks. For UI behavior run tests/browser against a disposable local catalog. The check runner drops only loopback databases ending _checks. Do not treat a build, health response or prerendered page as proof interactive filing works.

Origins stay on loopback. Production hosting requires signed Cloudflare Access tokens; keep the root, downloads and Blazor transport protected. Run tests/DocumentStorage.AccessChecks for hosting changes. Windows install/update scripts are in tools/deploy; their actual service/ACL behavior still needs server validation. NAS migration, Ollama, MCP and an in-app updater remain later milestones. Do not present roadmap controls as working AI.
