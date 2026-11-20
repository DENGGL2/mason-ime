-- Remove only the decorative learning and sentence comments, never candidate text.
local M = {}
local learning = require('mason_learning')
local hair_space = utf8.char(0x200A)
function M.init(env) learning.prepare(env) end
function M.func(input, env)
  learning.filter(input, env, function(cand)
    local comment = (cand.comment or ''):gsub(hair_space, ''):gsub('%s', '')
    if comment == '*' or comment == '∞' then cand.comment = '' end
    yield(cand)
  end)
end
return M
