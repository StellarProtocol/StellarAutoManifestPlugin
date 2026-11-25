namespace Stellar.AutoManifest;

// The game-side half of Auto Manifest: ONE Lua table `__stam` defined once (compile once — Plugin-Performance-Rules §4,
// Lua-Injection-from-CSharp "Polling Lua cheaply") and then driven by one-line DoString calls. Every result is PARKED
// in a global with rawset (the game's _G rejects plain writes — strict-global guard) and read back by C# through the
// type-strict ILua getters; nothing ever calls back into C# (a native Lua→C# callback crashes IL2CPP).
//
// Game calls used (release_4.7 Lua, verified 2026-11-21 — static, not live-validated):
//   reforge data   Z.ContainerMgr.CharSerialize.seasonTalentReforgeData.seasonTalentReforgeMap[seasonId]
//                  (season_talent_refine_sub_view.lua:967-972); seasonId = GetVM('season').GetCurrentSeasonId()
//                  (season_vm.lua:35); fields level / storeEntries / lastReforgeResults / lastReforgeTime
//                  (zcontainer/season_talent_reforge_season_data.lua); storeEntryLimit (…refine_storage_left_sub_view:99)
//   level row      SeasonTalentReforgeLevelTableMgr rows keyed by (Season, Level) (season_talent_entry_data.lua:116-138);
//                  DefaultConsume = {{itemId, count}, …} per pull (refine_sub_view.lua:827-850)
//   reachability   sd.exp = EXP INSIDE the current level, out of that level's UpgradeExp (refine_sub_view.lua:167-169);
//                  NormalWeight / PrimeWeight indexed by quality (entry_popup_view.lua:227-236); per-level gate =
//                  CheckCondition(row.UnlockCondition, false) like the Blessing gate; ExpAdd of item row 1220411
//   blessing gate  next level row exists AND CheckCondition(next.UnlockCondition, false) (refine_sub_view.lua:601-609)
//   row unlock     SeasonTalentReforgeAffixSlotTableMgr sorted by Id, CheckCondition(row.UnlockCondition, false)
//                  (season_talent_insert_window_view.lua:618-634)
//   roll           GetVM('season_talent_entry').AsyncSeasonTalentReforge(blessRows, times, token) (entry_vm.lua:246-260);
//                  blessRows = { SeasonTalentReforgeItemTableMgr.GetRow(1220411) } (refine_sub_view.lua:589-599, 693)
//   store          …AcyncStoreSeasonTalentReforgeEntry(pull0, row0, token) — "Acync" typo is real (entry_vm.lua:186)
//   discard        …AsyncRemoveStoredSeasonTalentReforgeEntry(uuid, token) (entry_vm.lua:200; request {storedEntryUid})
//                  = the pocket screen's own btn_discard path (season_talent_refine_storage_left_sub_view.lua:313-327)
//   pocket / char  sd.storeEntries = map of {uuid, entryId} (zcontainer/season_talent_stored_entry.lua: uuid field 1,
//                  entryId field 2); the store reply is `ret` only (world_proxy.lua:13897-13944 returns pbRet.ret), so
//                  the new entry's uuid comes from diffing the pocket around a store; CharSerialize.charId
//                  (zcontainer/char_serialize.lua:9-15, int64)
//   owned count    GetVM('items').GetItemTotalCount(id) (items_vm.lua:335)
//   looting class  GetVM('profession'):GetProfessionDropId() (profession_vm.lua:94) → ProfessionSystemTableMgr.Name (:113)
//   names / values GetVM('season_talent_entry').GetReforgeEntryAttrName(row) / GetReforgeEntryAttrValueText(row)
//                  (entry_vm.lua:20-30); class (buff) rows return '' → GetReforgeEntryAttrDesc(<row ID>) (:12),
//                  short name = the quoted span (live-validated 2026-11-21, KB Matrix-Manifestation §7c); category Lang('SeasonTalentAttrLibName_'..id) (entry_popup_view.lua:375)
//   item names     Z.TableMgr.GetRow('ItemTableMgr', id, true).Name (items_vm.lua:1268)
//   special items  SeasonTalentReforgeItemTableMgr rows with TypeEnum ~= E.SeasonTalentReforgeItemType.Blessing (4)
//                  (refine_sub_view.lua:297-330; enum_define.lua:1393 AttrLib=1 / Quality=2 / AttrType=3 / Blessing=4);
//                  usable = owned > 0 AND CheckCondition(row.Condition, false) (refine_sub_view.lua:697-709); the roll
//                  sends { blessing row, special row } (getRefineBlessItems, :589-599); enough = times <= owned (:497-507)
//   async          Z.CoroUtil.create_coro_xpcall(fn, errFn)() + Z.CancelSource.Rent() / :CreateToken() / :Recycle()
// ⚠️ Keep every string literal in SINGLE quotes: this is a C# verbatim string.
public sealed partial class Plugin
{
    private const int LuaScriptVersion = 7;

    private const string LuaScript = @"pcall(function()
local M = {}
local REM, BLESS = 1220006, 1220411
local function tbl(n) return Z.TableMgr.GetTable(n) end
local function entryVm() return Z.VMMgr.GetVM('season_talent_entry') end
local function count(id) return Z.VMMgr.GetVM('items').GetItemTotalCount(id) or 0 end
local function reforge()
  local cs = Z.ContainerMgr.CharSerialize
  local rd = cs and cs.seasonTalentReforgeData
  if rd == nil then return nil end
  local sid = Z.VMMgr.GetVM('season').GetCurrentSeasonId() or 0
  local map = rd.seasonTalentReforgeMap
  local sd = nil
  if map then sd = map[sid] end
  return rd, sd, sid
end
local levels = nil
local function levelRow(sid, lv)
  if levels == nil then
    levels = {}
    for _, row in pairs(tbl('SeasonTalentReforgeLevelTableMgr').GetDatas()) do
      if levels[row.Season] == nil then levels[row.Season] = {} end
      levels[row.Season][row.Level] = row
    end
  end
  local s = levels[sid]
  if s then return s[lv] end
  return nil
end
local slots = nil
local function slotRows()
  if slots == nil then
    slots = {}
    for _, row in pairs(tbl('SeasonTalentReforgeAffixSlotTableMgr').GetDatas()) do slots[#slots + 1] = row end
    table.sort(slots, function(a, b) return a.Id < b.Id end)
  end
  return slots
end
-- Special items (spec ⑬): every reforge item row that is not a Blessing, by Id like the game's own list.
local specs = nil
local function specRows()
  if specs == nil then
    specs = {}
    local e = rawget(_G, 'E')
    local bt = 4
    if e and e.SeasonTalentReforgeItemType and e.SeasonTalentReforgeItemType.Blessing then bt = e.SeasonTalentReforgeItemType.Blessing end
    for _, row in pairs(tbl('SeasonTalentReforgeItemTableMgr').GetDatas()) do
      if row.TypeEnum ~= bt then specs[#specs + 1] = row end
    end
    table.sort(specs, function(a, b) return a.Id < b.Id end)
  end
  return specs
end
function M.state()
  local rd, sd, sid = reforge()
  if rd == nil then rawset(_G, '__stam_s', '0') return end
  local lv = 1
  if sd and sd.level then lv = sd.level end
  -- Pocket: every stored entry as uuid:entryId. C# diffs it around a store to learn the new entry's uuid (the store
  -- reply is ret only) and checks a saved mark against it. Counted like the pocket view: entryId ~= 0.
  local stored, pocket = 0, {}
  if sd and sd.storeEntries then
    for _, e in pairs(sd.storeEntries) do
      if e.entryId ~= 0 then
        stored = stored + 1
        pocket[#pocket + 1] = tostring(e.uuid or 0) .. ':' .. tostring(e.entryId)
      end
    end
  end
  local cs = Z.ContainerMgr.CharSerialize
  local cid = '0'
  if cs and cs.charId then cid = tostring(cs.charId) end
  local row = levelRow(sid, lv)
  local cost, ok1, ok10 = 0, row ~= nil, row ~= nil
  if row then
    for _, c in ipairs(row.DefaultConsume) do
      if c[1] == REM then cost = cost + c[2] end
      local have = count(c[1])
      if have < c[2] then ok1 = false end
      if have < c[2] * 10 then ok10 = false end
    end
  end
  local nxt = levelRow(sid, lv + 1)
  local bless = nxt ~= nil and Z.ConditionHelper.CheckCondition(nxt.UnlockCondition, false)
  local mask = {}
  for i, s in ipairs(slotRows()) do
    if Z.ConditionHelper.CheckCondition(s.UnlockCondition, false) then mask[i] = '1' else mask[i] = '0' end
  end
  local prof = Z.VMMgr.GetVM('profession'):GetProfessionDropId() or 0
  -- Level gate per level (1-based): '1' = at or below the current level, or its UnlockCondition passes now.
  local lvmask = {}
  for l = 1, 99 do
    local r = levelRow(sid, l)
    if r == nil then break end
    if l <= lv or Z.ConditionHelper.CheckCondition(r.UnlockCondition, false) then lvmask[l] = '1' else lvmask[l] = '0' end
  end
  local exp = 0
  if sd and sd.exp then exp = sd.exp end
  -- Special items as id:owned:unlocked (unlocked = the row's Condition passes now, like getAvailableSelectedMaterial).
  local sp = {}
  for _, r in ipairs(specRows()) do
    -- pcall: one bad row must not abort the whole state line (an error here would leave __stam_s stale).
    local okp, okc = pcall(Z.ConditionHelper.CheckCondition, r.Condition, false)
    okc = okp and okc
    sp[#sp + 1] = r.Id .. ':' .. count(r.Id) .. ':' .. (okc and 1 or 0)
  end
  rawset(_G, '__stam_s', table.concat({ '1', sid, lv, rd.storeEntryLimit or 0, stored, count(REM), count(BLESS), cost,
    ok1 and 1 or 0, ok10 and 1 or 0, bless and 1 or 0, prof, table.concat(mask), exp, table.concat(lvmask), cid,
    table.concat(pocket, ','), table.concat(sp, ',') }, '|') .. '|')
end
-- Static special-item data, once per catalogue load: 'id,type,param1' US name, records RS-separated. param1 = the first
-- Param (TypeEnum 1 = the category's AttrLibId) or 0 when the row has none (1220401 Primary ships an EMPTY Param).
function M.specials()
  local out = {}
  for _, row in ipairs(specRows()) do
    local p = 0
    pcall(function() if row.Param and row.Param[1] then p = row.Param[1] end end)
    local n = ''
    local r = Z.TableMgr.GetRow('ItemTableMgr', row.Id, true)
    if r and r.Name then n = tostring(r.Name) end
    out[#out + 1] = row.Id .. ',' .. (row.TypeEnum or 0) .. ',' .. p .. '\31' .. n
  end
  rawset(_G, '__stam_sp', table.concat(out, '\30'))
end
-- The current season's level table, once per season: 'season,expPerBlessPull;lv,upgradeExp,n2,n3,n4,n5,p2,p3,p4,p5;…'.
-- NormalWeight / PrimeWeight are indexed BY QUALITY (entry_popup_view.lua:227-236: ipairs → quality), so [2..5] =
-- Superior..Immortal. ExpAdd = Matrix EXP per Blessing pull (SeasonTalentReforgeItemTable 1220411, 10 in S4).
function M.levels()
  local rd, _, sid = reforge()
  if rd == nil then rawset(_G, '__stam_lv', '') return end
  local add = 10
  local ir = tbl('SeasonTalentReforgeItemTableMgr').GetRow(BLESS, true)
  if ir and ir.ExpAdd then add = ir.ExpAdd end
  local out = { sid .. ',' .. add }
  for l = 1, 99 do
    local r = levelRow(sid, l)
    if r == nil then break end
    local f = { l, r.UpgradeExp or 0 }
    for q = 2, 5 do f[#f + 1] = (r.NormalWeight and r.NormalWeight[q]) or 0 end
    for q = 2, 5 do f[#f + 1] = (r.PrimeWeight and r.PrimeWeight[q]) or 0 end
    out[#out + 1] = table.concat(f, ',')
  end
  rawset(_G, '__stam_lv', table.concat(out, ';'))
end
function M.results()
  local rd, sd = reforge()
  if sd == nil then rawset(_G, '__stam_r', '') return end
  local parts = {}
  local res = sd.lastReforgeResults
  if res then
    for p, r in ipairs(res) do
      local ids = {}
      if r.entries then for k, e in ipairs(r.entries) do ids[k] = e.entryId or 0 end end
      parts[p] = table.concat(ids, ',')
    end
  end
  rawset(_G, '__stam_r', tostring(sd.lastReforgeTime or 0) .. '#' .. table.concat(parts, ';'))
end
local function run(fn)
  rawset(_G, '__stam_ret', nil)
  rawset(_G, '__stam_err', nil)
  local cs = Z.CancelSource.Rent()
  local done = false
  local function fin() if not done then done = true pcall(function() cs:Recycle() end) end end
  Z.CoroUtil.create_coro_xpcall(function()
    local ret = fn(cs:CreateToken())
    fin()
    rawset(_G, '__stam_ret', tonumber(ret) or -3)
  end, function(err)
    fin()
    rawset(_G, '__stam_err', tostring(err))
    rawset(_G, '__stam_ret', -1)
  end)()
end
-- special = a special item id (spec ⑬) or 0; sent after the Blessing row, like getRefineBlessItems.
function M.roll(bless, times, special)
  run(function(tok)
    local items = {}
    local it = tbl('SeasonTalentReforgeItemTableMgr')
    if bless then
      local row = it.GetRow(BLESS, true)
      if row == nil then return -2 end
      items[#items + 1] = row
    end
    if special and special ~= 0 then
      local row = it.GetRow(special, true)
      if row == nil then return -2 end
      items[#items + 1] = row
    end
    return entryVm().AsyncSeasonTalentReforge(items, times, tok)
  end)
end
function M.store(p, r)
  run(function(tok) return entryVm().AcyncStoreSeasonTalentReforgeEntry(p, r, tok) end)
end
-- Pocket discard (the pocket window's ✕, only after its confirm): u = storeEntries[*].uuid, the same field the game's
-- storage view keeps as selectedEntryUuid_. The VM shows the game's own result tip.
function M.discard(u)
  run(function(tok) return entryVm().AsyncRemoveStoredSeasonTalentReforgeEntry(u, tok) end)
end
function M.rows()
  local out = {}
  for _, row in pairs(tbl('SeasonTalentFactorAttrLibTableMgr').GetDatas()) do
    local t, v = 0, 0
    local ae = row.AttrEffect and row.AttrEffect[1]
    if ae then
      t = ae[1] or 0
      if t == 3 then
        local bp = row.BuffPar and row.BuffPar[1]
        if bp then v = bp[1] or 0 end
      else v = ae[3] or 0 end
    end
    out[#out + 1] = table.concat({ row.Id, row.Group, row.AffixType, row.AttrLibId, row.Profession or 0,
      row.AffixQuality, t, v, row.ExtremumTag or 0 }, ',')
  end
  rawset(_G, '__stam_rows', table.concat(out, ';'))
end
-- Class (buff) imprints: GetReforgeEntryAttrName returns '' (validated in game 2026-11-21); the only localized text is
-- GetReforgeEntryAttrDesc(<row ID>), e.g. <style=ItemQuality_2><color=#ffe2af>[Class]</color>Twin Striker's
-- ""Lucky Strike"" Oath DMG <style=""accent-gn"">+0.65%</style>.</style>  →  name 'Lucky Strike', kind 'Oath DMG'.
-- Quotes: "" “ ” 「 」 『 』 as byte escapes (no encoding round trip).
-- kind = the text AFTER the quoted name, cut at its first sentence end (EN table AttrDescription: 182 class rows are
-- '<Class>'s ""<skill>"" <effect> <value>.' — only the 10 shared 'Lucky Strike' rows carry the class possessive — plus
-- a few with a trailing note: '. (Excludes ""Instant Edge Combo"")', '.Upon entering combat…', '; Crit DMG is…').
-- Word order differs per language, so when nothing follows the name, the text BEFORE it is used minus the imprint's
-- own class name (ProfessionSystemTableMgr by row.Profession) and the possessive glued to it ('s / の / 의).
local QUOTES = { { '""', '""' }, { '\226\128\156', '\226\128\157' }, { '\227\128\140', '\227\128\141' },
  { '\227\128\142', '\227\128\143' } }
-- Sentence ends: '.' not followed by a digit (a decimal stays), '。', ';', '；'.
local CLAUSE_END = { '%.%D', '%.$', '\227\128\130', ';', '\239\188\155' }
local function trim(t)
  t = string.gsub(t, '%s+', ' ')
  t = string.gsub(t, '^[%s,:;]+', '')
  local prev
  repeat
    prev = t
    t = string.gsub(t, '[%s%.,:;!]+$', '')
    t = string.gsub(t, '\227\128\130$', '')
  until t == prev
  return t
end
local function firstClause(t)
  local cut = #t + 1
  for _, p in ipairs(CLAUSE_END) do
    local i = string.find(t, p)
    if i and i < cut then cut = i end
  end
  return string.sub(t, 1, cut - 1)
end
local function dropClass(t, cls)
  if cls == '' then return t end
  local i, j = string.find(t, cls, 1, true)
  if not i then return t end
  return string.sub(t, 1, i - 1) .. string.gsub(string.sub(t, j + 1), '^%S*', '', 1)
end
local function shortName(desc, cls)
  local s = string.gsub(desc, '<color=[^>]*>.-</color>', '', 1)
  s = string.gsub(s, '<style=""accent%-gn"">.-</style>', '')
  s = string.gsub(s, '<[^>]*>', '')
  for _, q in ipairs(QUOTES) do
    local a, b, inner = string.find(s, q[1] .. '(.-)' .. q[2])
    if a then
      local kind = trim(firstClause(string.sub(s, b + 1)))
      if kind == '' then kind = trim(dropClass(string.sub(s, 1, a - 1), cls)) end
      return trim(inner), kind
    end
  end
  return trim(s), ''
end
local function profName(id)
  if not id or id == 0 then return '' end
  local ok, r = pcall(function() return tbl('ProfessionSystemTableMgr').GetRow(id, true) end)
  if ok and r and r.Name then return tostring(r.Name) end
  return ''
end
function M.names(spec)
  local vm = entryVm()
  local tm = tbl('SeasonTalentFactorAttrLibTableMgr')
  local out = {}
  for g, ids in string.gmatch(spec, '(%d+):([%d,]+)') do
    local vals, name, kind, first = {}, '', '', true
    for id in string.gmatch(ids, '%d+') do
      local row = tm.GetRow(tonumber(id), true)
      local vt = ''
      if row then
        if first then
          first = false
          local ok, n = pcall(vm.GetReforgeEntryAttrName, row)
          if ok and n then name = tostring(n) end
          if name == '' then
            local okd, d = pcall(vm.GetReforgeEntryAttrDesc, tonumber(id))
            if okd and d then name, kind = shortName(tostring(d), profName(row.Profession)) end
          end
        end
        local ok, t = pcall(vm.GetReforgeEntryAttrValueText, row)
        if ok and t then vt = tostring(t) end
      end
      vals[#vals + 1] = vt
    end
    out[#out + 1] = g .. '\31' .. name .. '\31' .. kind .. '\31' .. table.concat(vals, '\31')
  end
  rawset(_G, '__stam_names', table.concat(out, '\30'))
end
function M.meta()
  local function L(k)
    local ok, has = pcall(function() return Z.LangMgr:IsContainKey(k) end)
    if ok and has then return tostring(Lang(k)) end
    return ''
  end
  local function item(id)
    local r = Z.TableMgr.GetRow('ItemTableMgr', id, true)
    if r and r.Name then return tostring(r.Name) end
    return ''
  end
  local out = {}
  for _, id in ipairs({ 1001, 101, 102, 103, 104, 105 }) do out[#out + 1] = L('SeasonTalentAttrLibName_' .. id) end
  out[#out + 1] = item(REM)
  out[#out + 1] = item(BLESS)
  rawset(_G, '__stam_meta', table.concat(out, '\31'))
end
function M.prof(id)
  local r = tbl('ProfessionSystemTableMgr').GetRow(id, true)
  local n = ''
  if r and r.Name then n = tostring(r.Name) end
  rawset(_G, '__stam_prof', n)
end
rawset(_G, '__stam', M)
rawset(_G, '__stam_v', 7)   -- = LuaScriptVersion: bump both together
end)";
}
