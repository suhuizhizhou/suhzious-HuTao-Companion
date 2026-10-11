using HuTao.Knowledge.Facts;
using HuTao.Knowledge.Retrieval;
using static HuTao.Knowledge.Storage.SqliteKnowledgeDatabase;

namespace HuTao.Knowledge.Graph;

public sealed record ExtractedEntity(string LocalId,string Name,string Type,int StartUtf16,int EndUtf16,string Quote);
public sealed record EntityProposalValidation(string LocalId,string Status,string Reason,ExtractionEntity? Entity,AssertionSource? Source);

public static class EntityProposalValidator
{
    public const string Version="entity-proposal-v2";
    public static EntityProposalValidation Validate(KnowledgeEvidence evidence,ExtractedEntity proposal)
    {
        EntityProposalValidation Reject(string reason)=>new(proposal.LocalId??"","rejected",reason,null,null);
        if(string.IsNullOrWhiteSpace(proposal.LocalId) || proposal.LocalId.Length>64 || !proposal.LocalId.StartsWith("new:",StringComparison.Ordinal))return Reject("invalid-local-id");
        if(string.IsNullOrWhiteSpace(proposal.Name) || proposal.Name.Length>100 || proposal.Name is "我" or "你" or "他" or "她" or "它" or "他们" or "它们")return Reject("unresolved-pronoun-or-name");
        if(proposal.Type is not ("person" or "character" or "organization" or "object" or "event" or "animal" or "location"))return Reject("unknown-entity-type");
        if(proposal.Quote!=proposal.Name)return Reject("entity-mention-mismatch");
        var start=proposal.StartUtf16;var end=proposal.EndUtf16;var repaired=false;
        if(start<0 || end<=start || end>evidence.Text.Length || evidence.Text[start..end]!=proposal.Quote)
        {
            // The exact surface string may unambiguously locate a mention despite a model counting error.
            // Never use fuzzy matching or choose arbitrarily among repeated occurrences.
            start=evidence.Text.IndexOf(proposal.Name,StringComparison.Ordinal);
            if(start<0 || evidence.Text.IndexOf(proposal.Name,start+1,StringComparison.Ordinal)>=0)return Reject("entity-mention-mismatch-or-ambiguous");
            end=start+proposal.Name.Length;repaired=true;
        }
        var identity=Hash($"{evidence.Reference.Namespace}\u001f{evidence.Reference.Id}\u001f{evidence.Reference.Revision}\u001f{start}\u001f{end}\u001f{proposal.Type}");
        return new(proposal.LocalId,"candidate",repaired?"unique-exact-quote-repaired-offsets-needs-review":"mention-grounded-type-and-identity-need-review",new("entity:"+identity,proposal.Type,proposal.Name),
            new(evidence.Reference,start,end,proposal.Quote));
    }
}
