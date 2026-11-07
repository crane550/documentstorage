using Microsoft.EntityFrameworkCore;
namespace DocumentStorage.Data;

public sealed class AssistantEdit
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Actor {get;set;}="";
    public Guid ConnectionId {get;set;}
    public string IdempotencyKey {get;set;}="";
    public string PayloadHash {get;set;}="";
    public string Action {get;set;}="";
    public string SourceRequest {get;set;}="";
    public string State {get;set;}="Applied";
    public string PayloadJson {get;set;}="{}";
    public string ContextHash {get;set;}="";
    public string AfterJson {get;set;}="{}";
    public string BeforeJson {get;set;}="{}";
    public DateTimeOffset? VerifiedAt {get;set;}
    public string VerifiedBy {get;set;}="";
    public string ResultJson {get;set;}="{}";
    public DateTimeOffset CreatedAt {get;set;}
}
public sealed class ReaderTeachingExample
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public Guid EntryId {get;set;}
    public Guid RootId {get;set;}
    public Guid KindId {get;set;}
    public string ContentHash {get;set;}="";
    public int DetailsRevision {get;set;}
    public string ChoiceJson {get;set;}="{}";
    public string TextExcerpt {get;set;}="";
    public string Lesson {get;set;}="";
    public string Actor {get;set;}="";
    public Guid ConnectionId {get;set;}
    public DateTimeOffset CreatedAt {get;set;}
    public DateTimeOffset? RemovedAt {get;set;}
    public int Revision {get;set;}=1;
}
public sealed partial class CatalogDb
{
    public DbSet<AssistantEdit> AssistantEdits=>Set<AssistantEdit>();
    public DbSet<ReaderTeachingExample> ReaderTeachingExamples=>Set<ReaderTeachingExample>();
    private static void ConfigureCoaching(ModelBuilder model)
    {
        model.Entity<AssistantEdit>().HasIndex(x=>new{x.Actor,x.ConnectionId,x.IdempotencyKey}).IsUnique();
        model.Entity<ReaderTeachingExample>().HasIndex(x=>new{x.RootId,x.CreatedAt});
        model.Entity<ReaderTeachingExample>().Property(x=>x.Revision).IsConcurrencyToken();
    }
}
