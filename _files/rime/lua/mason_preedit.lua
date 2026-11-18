-- Hide the decorative soft cursor without resetting the candidate menu on every key.
local M = {}

local function hide_cursor(context)
    if context:get_option("soft_cursor") then
        context:set_option("soft_cursor", false)
    end
end

function M.init(env)
    hide_cursor(env.engine.context)
end

function M.func(key, env)
    hide_cursor(env.engine.context)
    return 2 -- kNoop: keep the current page and continue normal key handling.
end

return M
