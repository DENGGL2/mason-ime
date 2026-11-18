-- Shift+Enter commits displayed pinyin. Remove its invisible colour metadata.
local M = {}
local marker = utf8.char(0x2063)
function M.init(env)
  env.enabled = env.engine.schema.config:get_bool('mason_correction/enabled') == true
end
function M.func(key, env)
  if not env.enabled or key:release() or not key:shift() or key:ctrl() or key:alt()
      or (key.keycode ~= 0xff0d and key.keycode ~= 0xff8d) then return 2 end
  local context = env.engine.context
  local cand = context:get_selected_candidate()
  if not cand or not cand.preedit or not cand.preedit:find(marker, 1, true) then return 2 end
  env.engine:commit_text((cand.preedit:gsub(marker, '')))
  context:clear()
  return 1
end
return M
