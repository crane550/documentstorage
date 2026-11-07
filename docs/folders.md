# Folder management

Open a folder from the sidebar tree, or visit `/library` (`/folders` opens the same page). The header shows the folder's path and its tools: **New folder**, **Upload**, and the ⋯ **Folder actions** menu. The details panel holds its inbox setting, the rules that file into it and its **Folder notes**.

**New folder** creates a real directory inside the open folder; its **Parent folder** button lets you choose another parent without losing the name you typed. **Rename or move folder** changes its name, its place in the same storage location, or both; the form shows the old and new paths. Its **Destination folder** button opens the shared folder tree, excluding the folder itself and everything inside it. All contents move together. Before and after a move, content verification covers up to 10,000 entries and 10 GB (including folder contents); larger operations must be split into smaller subfolders. Existing names are never overwritten and portable Windows/NAS filename rules apply on every server.

## Choose or create without leaving your work

The shared **Choose a folder** dialog is used by Inbox filing, rename/move, new-folder parents and rule destinations. Search at the top matches names or paths across all depths (including Windows-style separators). Matching folders stay beneath their parent branches, with full paths to distinguish identical names. Searches show up to 200 matching folders plus their ancestors and report when to narrow a large result set. Clearing search returns to normal expandable-tree browsing.

Select a folder, then **Use this folder**. The selected path also has clickable parent breadcrumbs. **New folder** opens a name box inside the selected folder; **Create folder** makes the real directory and reveals/selects it, ready to use or create another nested child. Names and conflicts are checked by Core. Creation rechecks the captured parent path; **Refresh folders** lets you recover from another tab moving or renaming it. Explicitly created folders remain on disk even if you later cancel the selection. Selecting a filing destination alone never moves the document.

Move/create pickers stay on their current storage. Inbox and rule destinations exclude inbox branches; Inbox filing requires a subfolder. Offline or paused storage stays visible but cannot be selected or used for creation. The picker rechecks selection eligibility and paths before returning them, and the eventual Core write retains its own validations.

Search receives focus. Arrow keys navigate the tree; Enter on a tree item chooses it. Enter in the new-folder name creates only the folder. Escape cancels the new-folder box first, then closes the picker, preserving the surrounding editor. Tab stays within the picker; background Inbox shortcuts are suspended while any dialog is open. On phones the tree scrolls within the dialog and the action bar stays accessible.

Storage roots remain fixed; create and rearrange their children. Offline roots are visible but cannot be edited. Cross-storage transfers are deferred until a verified copy/recovery contract is implemented. Moving a subtree that contains intake into another intake scope is rejected to preserve non-overlapping intake folders.

Folder and document IDs, saved instructions and intake flags survive app-managed renames/moves. Rules targeting these folder IDs continue to refer to them. Moves use the existing journal and recovery gate; unresolved operations block further writes in that storage. Open [Recovery](recovery.md) after an interrupted operation; paused storage is identified throughout the app. External Windows Explorer renames still have the reconciliation limitation described in the README.

Creating a child also checks that its parent still exists on disk, so a folder removed outside the app is not silently recreated.

Create, rename and move forms capture their source/parent paths. The writer rechecks them under its lock, so another tab cannot silently redirect a stale form after a folder moves. Validation errors keep the form open. Cancel and Escape discard an unsubmitted change; folders explicitly created inside the picker are already saved. Refresh the picker or reopen a stale form before trying again. Moves can change inherited guidance and whether ordinary documents are inside intake; inspect the displayed destination before saving.

Verification: `node tests/browser/folders.mjs` drives a disposable loopback app (default port 5242), checks real directory changes, descendants, collision/invalid-name rejection, preserved notes, competing tabs, direct navigation, sidebar access and mobile layout. The Core checks also cover stale source/destination/parent bindings and intake nesting, in addition to existing journal recovery checks. Windows service permissions and actual NAS availability still require server verification.

`node tests/browser/folder-picker.mjs` (default port 5252) covers search/disambiguation and result limits, nested dialog focus, real creation/conflicts, stale parents, moving through a newly created destination, rule drafts, offline storage, Inbox draft preservation, keyboard isolation and phone layout. `sorting.mjs` checks choosing an existing destination and returning to the kind-built path. Each fixture requires its own fresh disposable catalog and synthetic storage.
