-- Keep candidate text and meaningful comments; omit the legacy learning/sentence marks.
local M = {}
local hair_space = utf8.char(0x200A)
function M.init(env) end
function M.func(input, env)
  for cand in input:iter() do
    if cand.type == 'user_phrase' or cand.type == 'sentence' then
      local comment = (cand.comment or ''):gsub(hair_space, ''):gsub('%s', '')
      if comment == '*' or comment == '∞' then cand.comment = '' end
    end
    yield(cand)
  end
end
return M
