using System.Text.Json.Serialization;
namespace Romd.Contracts.Management.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateJobRequest([property: JsonRequired] bool IsArchived);
