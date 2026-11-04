# Folder management

Open a folder from the sidebar tree, or visit `/library` (`/folders` opens the same page). The header shows the folder's path and its tools: **New folder**, **Upload**, and the ⋯ **Folder actions** menu. The details panel holds its inbox setting, the rules that file into it and its **Folder notes**.

**New folder** creates a real directory inside the open folder. **Rename or move folder** changes its name, its place in the same storage location, or both; the form shows the old and new paths. The destination list can be filtered and excludes the folder itself and everything inside it. All contents move together. Before and after a move, content verification covers up to 10,000 entries and 10 GB (including folder contents); larger operations must be split into smaller subfolders. Existing names are never overwritten and portable Windows/NAS filename rules apply on every server.

Storage roots remain fixed; create and rearrange their children. Offline roots are visible but cannot be edited. Cross-storage transfers are deferred until a verified copy/recovery contract is implemented. Moving a subtree that contains intake into another intake scope is rejected to preserve non-overlapping intake folders.

Folder and document IDs, saved instructions and intake flags survive app-managed renames/moves. Rules targeting these folder IDs continue to refer to them. Moves use the existing journal and recovery gate; unresolved operations block further writes in that storage. Open [Recovery](recovery.md) after an interrupted operation; paused storage is identified throughout the app. External Windows Explorer renames still have the reconciliation limitation described in the README.

Creating a child also checks that its parent still exists on disk, so a folder removed outside the app is not silently recreated.

Create, rename and move forms capture their source/parent paths. The writer rechecks them under its lock, so another tab cannot silently redirect a stale form after a folder moves. Validation errors keep the form open. Cancel and Escape close it without changing storage. Refresh the page to reload after a stale-form error. Moves can change inherited guidance and whether ordinary documents are inside intake; inspect the displayed destination before saving.

Verification: `node tests/browser/folders.mjs` drives a disposable loopback app (default port 5242), checks real directory changes, descendants, collision/invalid-name rejection, preserved notes, competing tabs, direct navigation, sidebar access and mobile layout. The Core checks also cover stale source/destination/parent bindings and intake nesting, in addition to existing journal recovery checks. Windows service permissions and actual NAS availability still require server verification.
