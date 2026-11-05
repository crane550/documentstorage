using System.Text.Json;
using DocumentStorage.Data;
using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Core;

public sealed record OrganizationChoice(Guid DestinationFolderId,string NewFolder,string Filename,string DocumentType,string Note,bool Remember);
public sealed partial class LibraryService
{
    private async Task<(CatalogEntry Entry,StorageRoot Root,OllamaSettings Settings)> RequireOrganizationJobAsync(CatalogDb db,IdentificationJob job)
    {
        if(!await DescriptionMatchesAsync(db,job))throw new StorageException("The saved description changed. Suggest a folder again.");
        if(!job.Organizing||job.State!="Succeeded"||job.OrganizationJson.Length==0)throw new StorageException("Read this document with AI again for a current filing proposal.");
        var (entry,root)=await WritableEntryAsync(db,job.EntryId);await RequireIntakeAsync(db,entry);RequireStable(entry);
        var settings=await db.OllamaConfigurations.SingleAsync(x=>x.Id==1);
        var review=await db.Reviews.AsNoTracking().SingleOrDefaultAsync(x=>x.EntryId==entry.Id);
        if(!settings.Enabled||settings.Revision!=job.SettingsRevision||(review?.Revision??0)!=job.ReviewRevision||entry.RelativePath!=job.SourcePath||
            await FingerprintAsync(root,entry)!=job.ContentHash||(await OrganizationContextAsync(db,entry,settings)).Hash!=job.ContextHash)
            throw new StorageException("The document, folders, guidance or settings changed. Read with AI again before approving a filing plan.");
        return(entry,root,settings);
    }
    public async Task<OrganizationFiling> PreviewOrganizationFilingAsync(Guid jobId,OrganizationChoice choice,string actor)
    {
        Actor(actor);
        var filename=StoragePaths.ValidateName(choice.Filename);var create=choice.NewFolder.Trim();if(create.Length>0)create=StoragePaths.ValidateName(create);
        var type=FactKey(choice.DocumentType);var note=choice.Note.Trim();if(note.Length>1000)throw new StorageException("Keep your filing note under 1,000 characters.");
        await writer.WaitAsync();
        try
        {
            await using var db=await factory.CreateDbContextAsync();var job=await db.IdentificationJobs.SingleOrDefaultAsync(x=>x.Id==jobId)??throw new StorageException("Filing suggestion not found.");
            var (entry,root,settings)=await RequireOrganizationJobAsync(db,job);
            if(!string.Equals(Path.GetExtension(filename),Path.GetExtension(entry.Name),StringComparison.OrdinalIgnoreCase))throw new StorageException("Keep the original file extension.");
            var (parent,parentRoot)=await WritableEntryAsync(db,choice.DestinationFolderId);
            if(parentRoot.Id!=root.Id||!parent.IsFolder||parent.TrashBatchId!=null)throw new StorageException("Choose a folder in the same storage location.");
            var folders=await db.Entries.AsNoTracking().Where(x=>x.RootId==root.Id&&x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null).ToListAsync();
            if(HasIntakeAncestor(parent,folders))throw new StorageException("Choose a destination outside the inbox so the document leaves the queue.");
            if(!Directory.Exists(StoragePaths.Resolve(root.Location,parent.RelativePath)))throw new StorageException("The destination folder is unavailable. Rescan before approving.");
            if(create.Length>0)
            {
                var existing=folders.FirstOrDefault(x=>x.ParentId==parent.Id&&string.Equals(x.Name,create,StringComparison.OrdinalIgnoreCase));
                if(existing!=null)
                {
                    if(HasIntakeAncestor(existing,folders))throw new StorageException("Choose a destination outside the inbox so the document leaves the queue.");
                    parent=existing;create="";
                }
            }
            var targetFolder=create.Length==0?parent.RelativePath:StoragePaths.Join(parent.RelativePath,create);
            if(create.Length>0&&(StoragePaths.Exists(StoragePaths.Resolve(root.Location,targetFolder))||await db.Entries.AnyAsync(x=>x.RootId==root.Id&&x.RelativePath==targetFolder)))
                throw new StorageException("The proposed subfolder already exists or is reserved. Rescan and select the existing folder.");
            var target=StoragePaths.Join(targetFolder,filename);
            if(StoragePaths.Exists(StoragePaths.Resolve(root.Location,target))||await db.Entries.AnyAsync(x=>x.RootId==root.Id&&x.RelativePath==target))
                throw new StorageException("An item with that filename already exists or is reserved. Choose another name; nothing will be overwritten.");
            var suggestion=JsonSerializer.Deserialize<OrganizationSuggestion>(job.OrganizationJson)!;
            var plan=new OrganizationFiling {JobId=job.Id,EntryId=entry.Id,Actor=actor,SourcePath=entry.RelativePath,ContentHash=job.ContentHash,ContextHash=job.ContextHash,
                ReviewRevision=job.ReviewRevision,SettingsRevision=job.SettingsRevision,ParentFolderId=parent.Id,ParentPath=parent.RelativePath,NewFolderName=create,
                TargetFolderId=create.Length==0?parent.Id:Guid.NewGuid(),Filename=filename,DestinationPath=target,DocumentType=type,FactsJson=job.FactsJson,
                Reason=note.Length==0?suggestion.Reason:note,Remember=choice.Remember,CreatedAt=time.GetUtcNow(),ExpiresAt=time.GetUtcNow().AddMinutes(15)};
            db.OrganizationFilings.Add(plan);await db.SaveChangesAsync();return plan;
        }
        finally {writer.Release();}
    }
    public async Task<Guid> ApplyOrganizationFilingAsync(Guid planId,string actor)
    {
        Actor(actor);await writer.WaitAsync();
        try
        {
            await using var db=await factory.CreateDbContextAsync();var plan=await db.OrganizationFilings.SingleOrDefaultAsync(x=>x.Id==planId)??throw new StorageException("Review a filing proposal first.");
            if(plan.Actor!=actor)throw new StorageException("This filing approval belongs to another sign-in.");
            if(plan.State=="Filed"&&plan.OperationId is Guid done)return done;
            if(plan.State!="Pending"||plan.OperationId!=null)throw new StorageException("This filing attempt needs Recovery or a fresh proposal; it cannot be executed twice.");
            if(plan.ExpiresAt<=time.GetUtcNow())throw new StorageException("This filing preview expired. Review the proposed move again.");
            var job=await db.IdentificationJobs.SingleAsync(x=>x.Id==plan.JobId);var(entry,root,_)=await RequireOrganizationJobAsync(db,job);
            if(job.ContentHash!=plan.ContentHash||job.ContextHash!=plan.ContextHash||job.ReviewRevision!=plan.ReviewRevision||job.SettingsRevision!=plan.SettingsRevision||entry.RelativePath!=plan.SourcePath)
                throw new StorageException("The filing plan no longer matches this document. Read it again.");
            var(parent,parentRoot)=await WritableEntryAsync(db,plan.ParentFolderId);
            var folders=await db.Entries.AsNoTracking().Where(x=>x.RootId==root.Id&&x.IsFolder&&!x.IsMissing&&x.TrashBatchId==null).ToListAsync();
            if(parentRoot.Id!=root.Id||parent.RelativePath!=plan.ParentPath||!parent.IsFolder||parent.TrashBatchId!=null||HasIntakeAncestor(parent,folders))throw new StorageException("The destination changed. Review the proposed move again.");
            if(plan.NewFolderName.Length>0)
            {
                var folderPath=StoragePaths.Join(parent.RelativePath,plan.NewFolderName);
                if(StoragePaths.Exists(StoragePaths.Resolve(root.Location,folderPath))||await db.Entries.AnyAsync(x=>x.RootId==root.Id&&x.RelativePath==folderPath))throw new StorageException("The proposed folder now exists. Rescan and choose it explicitly.");
            }
            var op=new FileOperation {Kind="Organize",RootId=root.Id,EntryId=entry.Id,FromPath=plan.SourcePath,ToPath=plan.DestinationPath,TargetParentId=plan.TargetFolderId,OrganizationFilingId=plan.Id,CreatedAt=time.GetUtcNow()};
            plan.State="Applying";plan.OperationId=op.Id;
            await ExecuteMoveAsync(db,root,entry,op);
            return op.Id;
        }
        finally {writer.Release();}
    }
    private async Task EnsureOrganizationFolderAsync(CatalogDb db,StorageRoot root,FileOperation op)
    {
        if(op.OrganizationFilingId is not Guid id)return;
        var plan=await db.OrganizationFilings.SingleAsync(x=>x.Id==id);
        if(plan.NewFolderName.Length==0)return;
        var relative=StoragePaths.Join(plan.ParentPath,plan.NewFolderName);var path=StoragePaths.Resolve(root.Location,relative);
        if(StoragePaths.Exists(path)||await db.Entries.AnyAsync(x=>x.RootId==root.Id&&x.RelativePath==relative))throw new StorageException("A folder appeared at the approved destination. Inspect this operation in Recovery.");
        Directory.CreateDirectory(path);
        db.Entries.Add(new() {Id=plan.TargetFolderId,RootId=root.Id,ParentId=plan.ParentFolderId,RelativePath=relative,Name=plan.NewFolderName,IsFolder=true,ModifiedAt=time.GetUtcNow()});
        await db.SaveChangesAsync();
    }
    private async Task CompleteOrganizationAsync(CatalogDb db,FileOperation op)
    {
        if(op.OrganizationFilingId is not Guid id)return;
        var plan=await db.OrganizationFilings.SingleAsync(x=>x.Id==id);var job=await db.IdentificationJobs.SingleAsync(x=>x.Id==plan.JobId);
        plan.State="Filed";plan.CompletedAt=time.GetUtcNow();plan.OperationId=op.Id;job.State="Filed";
        if(plan.Remember&&!await db.FilingExamples.AnyAsync(x=>x.Id==plan.Id))db.FilingExamples.Add(new() {Id=plan.Id,RootId=op.RootId,EntryId=op.EntryId,FolderId=plan.TargetFolderId,OriginalName=Path.GetFileName(plan.SourcePath),
            Filename=plan.Filename,DocumentType=plan.DocumentType,Summary=job.Summary,Note=plan.Reason,Actor=plan.Actor,CreatedAt=time.GetUtcNow()});
    }
    private async Task CancelOrganizationAsync(CatalogDb db,FileOperation op)
    {
        if(op.OrganizationFilingId is not Guid id)return;
        var plan=await db.OrganizationFilings.SingleAsync(x=>x.Id==id);plan.State="Cancelled";plan.CompletedAt=time.GetUtcNow();
        var job=await db.IdentificationJobs.SingleAsync(x=>x.Id==plan.JobId);job.State="Rejected";job.LastError="The interrupted filing was cancelled in Recovery. Request a new proposal to retry.";
    }
    public async Task RejectOrganizationAsync(Guid jobId,string actor)
    {
        Actor(actor);await writer.WaitAsync();
        try {await using var db=await factory.CreateDbContextAsync();var job=await db.IdentificationJobs.SingleAsync(x=>x.Id==jobId);
            if(!job.Organizing||job.State!="Succeeded")throw new StorageException("This proposal is no longer awaiting review.");
            if(await db.OrganizationFilings.AnyAsync(x=>x.JobId==jobId&&x.State=="Applying"))throw new StorageException("A filing attempt is already in progress. Check Recovery.");
            job.State="Rejected";job.LastError="Filing suggestion declined. Read again when you want a new proposal.";await db.SaveChangesAsync();}
        finally {writer.Release();}
    }
    public async Task<List<FilingExample>> FilingExamplesAsync()
    {await using var db=await factory.CreateDbContextAsync();return await db.FilingExamples.AsNoTracking().Where(x=>x.RemovedAt==null).OrderByDescending(x=>x.CreatedAt).Take(30).ToListAsync();}
    public async Task ForgetFilingExampleAsync(Guid id,string actor)
    {
        Actor(actor);await writer.WaitAsync();
        try {await using var db=await factory.CreateDbContextAsync();var example=await db.FilingExamples.SingleAsync(x=>x.Id==id);example.RemovedAt=time.GetUtcNow();example.RemovedBy=actor;await db.SaveChangesAsync();}
        finally {writer.Release();}
    }
}
