using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Core;

public sealed partial class LibraryService
{
    private const string PlanMarker=".documentstorage-folder-plan";
    private static async Task<(CatalogEntry Scope,List<CatalogEntry> Folders)> PlanningFoldersAsync(CatalogDb db,Guid scopeId)
    {
        var scope=await db.Entries.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==scopeId);
        if(scope==null||!scope.IsFolder||scope.IsMissing||scope.TrashBatchId!=null)throw new StorageException("Choose an available parent folder.");
        var all=await db.Entries.AsNoTracking().Where(x=>x.RootId==scope.RootId&&x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null).Select(x=>new CatalogEntry{Id=x.Id,RootId=x.RootId,ParentId=x.ParentId,Name=x.Name,RelativePath=x.RelativePath,Instructions=x.Instructions,IsFolder=true,IsIntake=x.IsIntake}).ToListAsync();
        if(HasIntakeAncestor(scope,all))throw new StorageException("Plan destination folders outside the inbox.");
        var prefix=scope.RelativePath.Length==0?"":scope.RelativePath+"/";
        return(scope,all.Where(x=>(x.Id==scope.Id||x.RelativePath.StartsWith(prefix,StringComparison.Ordinal))&&!HasIntakeAncestor(x,all)).OrderBy(x=>x.RelativePath,StringComparer.Ordinal).ToList());
    }
    private async Task<(string Json,string Hash)> FolderPlanningContextAsync(CatalogDb db,Guid scopeId,OllamaSettings settings)
    {
        var (scope,folders)=await PlanningFoldersAsync(db,scopeId);var root=await db.Roots.AsNoTracking().SingleAsync(x=>x.Id==scope.RootId);
        var selected=new List<object>();var length=0;foreach(var f in folders)
        {
            var row=new{id=f.Id,parent=f.ParentId,name=f.Name,path=f.RelativePath,guidance=f.Instructions[..Math.Min(500,f.Instructions.Length)]};var size=JsonSerializer.Serialize(row).Length;
            if(selected.Count>=150||length+size>13000)continue;selected.Add(row);length+=size;
        }
        var json=JsonSerializer.Serialize(new{scope_id=scope.Id,root_name=root.Name,guidance=settings.Instructions,folders=selected,truncated=selected.Count<folders.Count});
        return(json,Digest(JsonSerializer.Serialize(new{scope.Id,scope.RelativePath,root.Name,settings.Revision,settings.Instructions,folders=folders.Select(x=>new{x.Id,x.ParentId,x.RelativePath,x.Instructions,x.IsIntake})})));
    }
    private async Task<List<FolderPlanStep>> ResolveFolderPlanAsync(CatalogDb db,Guid scopeId,List<FolderNode> nodes)
    {
        if(nodes.Count>24)throw new StorageException("Plan at most 24 folders at a time.");
        var(scope,folders)=await PlanningFoldersAsync(db,scopeId);var known=folders.ToDictionary(x=>x.Id);var resolved=new Dictionary<string,FolderPlanStep>();var used=new HashSet<Guid>();var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var steps=new List<FolderPlanStep>();
        foreach(var node in nodes)
        {
            if(!Regex.IsMatch(node.Key,"^F[1-9][0-9]?$",RegexOptions.CultureInvariant)||resolved.ContainsKey(node.Key))throw new StorageException("Folder plan keys must be unique F1, F2, etc.");
            var name=StoragePaths.ValidateName(node.Name);var notes=node.Instructions.Trim();if(notes.Length>2000||notes.Contains('\0'))throw new StorageException("Keep each planned folder's instructions under 2,000 characters without null characters.");
            Guid parentId;string parentPath;
            if(Guid.TryParse(node.Parent,out var parent)&&known.TryGetValue(parent,out var existingParent)){parentId=parent;parentPath=existingParent.RelativePath;}
            else if(resolved.TryGetValue(node.Parent,out var earlier)){parentId=earlier.FolderId;parentPath=earlier.Path;}
            else throw new StorageException("Choose an existing parent inside the selected scope or an earlier planned folder.");
            var relative=StoragePaths.Join(parentPath,name);if(relative.Split('/').Length-(scope.RelativePath.Length==0?0:scope.RelativePath.Split('/').Length)>8)throw new StorageException("Plan no more than eight new folder levels at once.");
            var create=node.ExistingId.Length==0;var folderId=Guid.NewGuid();
            if(!create)
            {
                if(!Guid.TryParse(node.ExistingId,out folderId)||!known.TryGetValue(folderId,out var existing)||existing.Id==scopeId||existing.ParentId!=parentId||!string.Equals(existing.Name,name,StringComparison.OrdinalIgnoreCase)||!used.Add(folderId))throw new StorageException("An existing planned folder must keep its current name and parent, and appear only once.");
                relative=existing.RelativePath;name=existing.Name;if(notes.Length==0)notes=existing.Instructions;
            }
            else if(folders.Any(x=>string.Equals(x.RelativePath,relative,StringComparison.OrdinalIgnoreCase))||await db.Entries.AnyAsync(x=>x.RootId==scope.RootId&&x.RelativePath==relative))throw new StorageException("That planned folder already exists or is reserved. Ask the reader to reuse it, or choose another name.");
            if(!paths.Add(relative))throw new StorageException("Two planned folders have the same path.");
            var step=new FolderPlanStep{Ordinal=steps.Count,RootId=scope.RootId,FolderId=folderId,ParentId=parentId,Path=relative,Name=name,Instructions=notes,Create=create};steps.Add(step);resolved.Add(node.Key,step);
        }
        return steps;
    }
    public async Task<FolderPlanApproval> PreviewFolderPlanAsync(Guid turnId,List<FolderNode> nodes,string actor)
    {
        Actor(actor);if(nodes.Count==0)throw new StorageException("Add at least one folder to the plan.");await writer.WaitAsync();
        try
        {
            await using var db=await factory.CreateDbContextAsync();var turn=await OwnedTurnAsync(db,turnId,actor);var conversation=await OwnedConversationAsync(db,turn.ConversationId,actor);
            if(conversation.Mode!="folders"||turn.State!="Answered"||conversation.ScopeEntryId==null||await db.DeskTurns.AnyAsync(x=>x.ConversationId==conversation.Id&&x.CreatedAt>turn.CreatedAt))throw new StorageException("Review the latest folder-plan reply before creating folders.");
            var settings=await AiDefaultsAsync(db);var context=await FolderPlanningContextAsync(db,conversation.ScopeEntryId.Value,settings);
            if(context.Hash!=turn.ContextHash||settings.Revision!=turn.SettingsRevision)throw new StorageException("The tree, notes or reader settings changed. Ask for an updated plan.");
            var steps=await ResolveFolderPlanAsync(db,conversation.ScopeEntryId.Value,nodes);
            foreach(var step in steps){var(_,root)=await WritableEntryAsync(db,conversation.ScopeEntryId.Value);if(step.Create&&StoragePaths.Exists(StoragePaths.Resolve(root.Location,step.Path)))throw new StorageException("A planned path already exists on storage. Rescan and update the plan.");}
            var approval=new FolderPlanApproval{TurnId=turnId,ScopeId=conversation.ScopeEntryId.Value,Actor=actor,ExpectedHash=context.Hash,ExpiresAt=time.GetUtcNow().AddMinutes(15)};
            foreach(var step in steps)step.ApprovalId=approval.Id;
            db.FolderPlanApprovals.Add(approval);db.FolderPlanSteps.AddRange(steps);await db.SaveChangesAsync();return approval;
        }finally{writer.Release();}
    }
    public async Task ApplyFolderPlanAsync(Guid approvalId,string actor)
    {
        Actor(actor);await writer.WaitAsync();
        try
        {
            await using var db=await factory.CreateDbContextAsync();var approval=await db.FolderPlanApprovals.SingleOrDefaultAsync(x=>x.Id==approvalId&&x.Actor==actor)??throw new StorageException("Folder plan not found for this sign-in.");
            if(approval.State=="Applied")return;
            // The writer proves another apply call is no longer active, even if a failed save left Applying behind.
            if(approval.State is not ("Pending" or "Paused" or "Applying"))throw new StorageException("This plan is not available to apply.");
            if(approval.State=="Pending"&&approval.ExpiresAt<=time.GetUtcNow())throw new StorageException("The folder preview expired. Review the plan again.");
            var turn=await OwnedTurnAsync(db,approval.TurnId,actor);
            var conversation=await OwnedConversationAsync(db,turn.ConversationId,actor);
            if(conversation.Archived||await db.DeskTurns.AnyAsync(x=>x.ConversationId==turn.ConversationId&&x.CreatedAt>turn.CreatedAt))throw new StorageException("A newer conversation request superseded this approval. Keep completed folders, cancel remaining steps and review the latest plan.");
            var settings=await AiDefaultsAsync(db);var(_,root)=await WritableEntryAsync(db,approval.ScopeId);
            if((await FolderPlanningContextAsync(db,approval.ScopeId,settings)).Hash!=approval.ExpectedHash)throw new StorageException("The tree or instructions changed. Keep the completed folders and request an updated plan.");
            var steps=await db.FolderPlanSteps.Where(x=>x.ApprovalId==approval.Id).OrderBy(x=>x.Ordinal).ToListAsync();
            approval.State="Applying";approval.Error=null;await db.SaveChangesAsync();
            try
            {
                foreach(var step in steps.Where(x=>x.State!="Completed"))
                {
                    await RequireOnlineAsync(root);
                    if((await FolderPlanningContextAsync(db,approval.ScopeId,settings)).Hash!=approval.ExpectedHash)throw new StorageException("The folder tree or notes changed while applying. Completed folders have been kept.");
                    var parent=await db.Entries.AsNoTracking().SingleAsync(x=>x.Id==step.ParentId);
                    if(StoragePaths.Join(parent.RelativePath,step.Name)!=step.Path||!Directory.Exists(StoragePaths.Resolve(root.Location,parent.RelativePath)))throw new StorageException("A planned parent changed or is unavailable.");
                    var path=StoragePaths.Resolve(root.Location,step.Path);var marker=StoragePaths.Resolve(root.Location,StoragePaths.Join(step.Path,PlanMarker));
                    if(!step.Create&&!Directory.Exists(path))throw new StorageException("A folder selected for reuse is unavailable. Rescan and update the plan before saving its notes.");
                    if(step.Create)
                    {
                        if(step.State=="Pending"){if(StoragePaths.Exists(path))throw new StorageException("A folder appeared at a planned path; nothing was replaced.");step.State="Creating";await db.SaveChangesAsync();}
                        if(StoragePaths.Exists(path))
                        {
                            if(!Directory.Exists(path)||!File.Exists(marker)||(await File.ReadAllTextAsync(marker)).Trim()!=step.Id.ToString())throw new StorageException("This path cannot be confirmed as a folder created by this plan. Preserve it and request an updated plan.");
                        }
                        else
                        {
                            var stageRelative=StoragePaths.Join(StoragePaths.Staging,"folder-plan-"+step.Id.ToString("N"));var stage=StoragePaths.Resolve(root.Location,stageRelative);
                            Directory.CreateDirectory(stage);await File.WriteAllTextAsync(StoragePaths.Resolve(root.Location,StoragePaths.Join(stageRelative,PlanMarker)),step.Id.ToString());
                            Directory.Move(stage,path);
                        }
                    }
                    await using(var transaction=await db.Database.BeginTransactionAsync())
                    {
                        var folder=await db.Entries.SingleOrDefaultAsync(x=>x.Id==step.FolderId);
                        if(step.Create){if(folder!=null||await db.Entries.AnyAsync(x=>x.RootId==root.Id&&x.RelativePath==step.Path))throw new StorageException("The folder identity or path is already held. Nothing was overwritten.");folder=new(){Id=step.FolderId,RootId=root.Id,ParentId=step.ParentId,Name=step.Name,RelativePath=step.Path,IsFolder=true,ModifiedAt=time.GetUtcNow()};db.Entries.Add(folder);}
                        if(folder==null||folder.IsMissing||folder.TrashBatchId!=null||folder.RelativePath!=step.Path)throw new StorageException("The planned folder changed.");
                        if(folder.Instructions!=step.Instructions)
                        {
                            var source=new InstructionMessage{TargetKind="folder",TargetKey=folder.Id.ToString(),Role="user",Text=turn.Question,Actor=actor,CreatedAt=time.GetUtcNow()};db.InstructionMessages.Add(source);folder.Instructions=step.Instructions;
                            await RecordInstructionAsync(db,new("folder",folder.Id.ToString()),step.Instructions,actor,[source.Id]);
                        }
                        step.State="Completed";await db.SaveChangesAsync();
                        approval.ExpectedHash=(await FolderPlanningContextAsync(db,approval.ScopeId,settings)).Hash;await db.SaveChangesAsync();await transaction.CommitAsync();
                    }
                    if(step.Create)try{File.Delete(marker);}catch(IOException){}catch(UnauthorizedAccessException){}
                }
                approval.State="Applied";turn.State="Applied";await db.SaveChangesAsync();
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or StorageException)
            {
                approval.State="Paused";approval.Error=ex is StorageException?ex.Message:"Folder creation paused. Completed folders are kept; restore storage access and resume.";await db.SaveChangesAsync();throw new StorageException(approval.Error);
            }
        }finally{writer.Release();}
    }
    public async Task CancelFolderPlanAsync(Guid id,string actor)
    {
        await writer.WaitAsync();try{await using var db=await factory.CreateDbContextAsync();var approval=await db.FolderPlanApprovals.SingleOrDefaultAsync(x=>x.Id==id&&x.Actor==actor)??throw new StorageException("Plan not found.");if(approval.State=="Applied")return;approval.State="Cancelled";await db.FolderPlanSteps.Where(x=>x.ApprovalId==id&&x.State!="Completed").ExecuteUpdateAsync(s=>s.SetProperty(x=>x.State,"Cancelled"));await db.SaveChangesAsync();}finally{writer.Release();}
    }
}
