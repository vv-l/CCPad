using CCPad.CodexQuota;
using System.Text.Json;
using System.Text.Json.Nodes;
var root = Path.Combine(Path.GetTempPath(), "ccpad-registry-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var store = new CodexQuotaProfileStore(root, Path.Combine(root, "no-auth"));
var registry = new AccountUsageRegistry(store, root);
var file = Path.Combine(root, "account-usage.json");
int checks=0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
JsonObject Entry(string id="xagents", string account="default") => new() {
 ["entry_id"]=id,["account_ref"]=account,["account_label"]="Codex-A",["bot_ref"]=id,
 ["provider"]="openai-codex",["capability"]=new JsonArray("remote-cli"),["host_ref"]="167",
 ["data_home_ref"]="CODEX_HOME:profile-a",["model"]="unknown",["reasoning"]="unknown",
 ["fast"]="unknown",["service_tier"]="unknown",["usage_state"]="not-checked",
 ["observed_at"]="2026-10-12T10:00:00-04:00",["evidence_ref"]=new JsonArray("evidence/status.json") };
void Save(params JsonObject[] entries) => File.WriteAllText(file,new JsonObject { ["registry_version"]=1,["entries"]=new JsonArray(entries.Select(e=>(JsonNode)e).ToArray()) }.ToJsonString());
Check(registry.GetAllAccountUsage().State=="missing","missing empty");
Save(Entry(),Entry("autobot"));
Check(registry.GetAccountUsage("default").Entries.Length==2,"multi Bot per profile");
Check(registry.GetAccountUsage("default").Entries.All(e=>e.ProfileState=="available" && e.Entry.UsageState=="not-checked"),"existence never ready");
Check(registry.GetAccountUsage("absent").Entries.Length==0,"exact reference no fallback");
var chinese=Entry(); chinese["account_label"]="Windows 专业账号"; chinese["bot_ref"]="智能助手 Bot"; Save(chinese);
Check(registry.GetAllAccountUsage().Entries.Single().Entry.BotRef=="智能助手 Bot", "Unicode human labels preserved");
chinese=Entry(); chinese["account_ref"]="中文ID"; Save(chinese);
Check(registry.GetAllAccountUsage().State=="invalid", "reference IDs remain strict");
chinese=Entry(); chinese["bot_ref"]="助手\n伪造"; Save(chinese);
Check(registry.GetAllAccountUsage().State=="invalid", "label control characters rejected");
Save(Entry(account:"unknown"));
Check(registry.GetAllAccountUsage().Entries.Single().ProfileState=="unavailable","missing profile unavailable");
Save(Entry(),Entry()); Check(registry.GetAllAccountUsage().State=="invalid","duplicate entry rejected");
foreach(var key in new[]{"token","auth","account_id","home"}) { var e=Entry(); e[key]="FAKE_SENTINEL";Save(e);Check(registry.GetAllAccountUsage().Entries.Length==0,"reject field " + key); }
foreach(var pair in new[]{("account_label","a@example.test"),("data_home_ref","C:/private"),("evidence_ref","../auth.json"),("observed_at","2026-10-12T10:00:00")}) {
 var e=Entry();e[pair.Item1]=pair.Item1=="evidence_ref"?new JsonArray(pair.Item2):JsonValue.Create(pair.Item2);Save(e);Check(registry.GetAllAccountUsage().State=="invalid","reject " + pair.Item1);
}
File.WriteAllText(file,"{bad"); Check(registry.GetAllAccountUsage().State=="invalid","parse failure isolated");
Check(AccountUsageDisplay.Format(registry.GetAllAccountUsage(), "{0}", "未知", "未登记", "不可用")=="不可用", "invalid Registry does not hide quota UI");
Save(Entry(), Entry("second"));
var rendered=AccountUsageDisplay.Format(registry.GetAllAccountUsage(), "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}", "未知", "未登记", "不可用");
Check(rendered.Contains("second") && rendered.Contains("未知") && rendered.Contains("2026-10-12T10:00:00-04:00") && rendered.Contains("evidence/status.json"), "summary includes all Bots unknowns time and evidence");
var fast=Entry();fast["fast"]=false;Save(fast);
Check(AccountUsageDisplay.Format(registry.GetAllAccountUsage(), "{4}", "未知", "未登记", "不可用")=="false", "false fast remains false");
Save(Entry()); File.WriteAllText(Path.Combine(root,"quota-accounts.json"),"{bad");
Check(registry.GetAllAccountUsage().State=="profiles-unavailable","profile store failure isolated");
Check(!JsonSerializer.Serialize(registry.GetAllAccountUsage()).Contains(root),"DTO excludes absolute home");
Console.WriteLine($"{checks}/{checks} checks passed; realCredentialsRead=0; realResetRequests=0");
