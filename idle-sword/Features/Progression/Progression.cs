using IdleSword.Core;

namespace IdleSword.Features;

public sealed partial class GameSession
{
    public double TalentBonus(string effect) => Config.Rows("Talent").Where(r => r.Text("effect") == effect).Sum(r => r.Number("value") * State.Talents.GetValueOrDefault(r.Text("id")));
    public double SkillBonus(string skill) => Config.Rows("SwordUpgrade").Where(r => r.Text("skill_id") == skill).Sum(r => r.Number("value") * State.Upgrades.GetValueOrDefault(r.Text("id")));
    public bool TalentVisible(string id) => State.Talents.GetValueOrDefault(id) > 0 ||
        !Config.Rows("TalentLink").Any(l => l.Text("to_id") == id) ||
        Config.Rows("TalentLink").Any(l => l.Text("to_id") == id && State.Talents.GetValueOrDefault(l.Text("from_id")) > 0);
    private bool Pay(string currency, double amount)
    {
        if (State.Amount(currency) < amount) return false;
        State.Wallet[currency] = State.Amount(currency) - amount; return true;
    }
}
