-- Remove only the decorative learning and sentence comments, never candidate text.
local M = {}
local hair_space = utf8.char(0x200A)
function M.func(input, env)
  for cand in input:iter() do
    local comment = (cand.comment or ''):gsub(hair_space, ''):gsub('%s', '')
    if comment == '*' or comment == '∞' then cand.comment = '' end
    yield(cand)
  end
end
return M
