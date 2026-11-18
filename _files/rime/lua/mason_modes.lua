-- Tray controls are applied before the next key reaches Rime's input processors.
-- Only Mason's four input options are synchronized; other input methods are untouched.
local M = {}
local names = { 'ascii_mode', 'traditionalization', 'full_shape', 'chinese_english' }
local tracked = {}
for _, name in ipairs(names) do tracked[name] = true end

local function read(path)
  local file = io.open(path, 'rb')
  if not file then return nil end
  local text = file:read(2049)
  file:close()
  if not text or #text > 2048 then return nil end
  local fields = {}
  for line in text:gmatch('[^\r\n]+') do
    local key, value = line:match('^([a-z_]+)=([a-zA-Z0-9_-]+)$')
    if not key or fields[key] then return nil end
    fields[key] = value
  end
  local revision = fields.revision
  if not revision or #revision > 80 or fields['end'] ~= revision then return nil end
  local state = { revision = revision }
  for _, name in ipairs(names) do
    if fields[name] ~= '0' and fields[name] ~= '1' then return nil end
    state[name] = fields[name] == '1'
  end
  return state
end

local function snapshot(env, context)
  if env.syncing then return end
  local revision = env.command_revision or 'initial'
  local lines = { 'revision=' .. revision }
  for _, name in ipairs(names) do lines[#lines + 1] = name .. '=' .. (context:get_option(name) and '1' or '0') end
  lines[#lines + 1] = 'end=' .. revision
  local text = table.concat(lines, '\n') .. '\n'
  local current = io.open(env.state_path, 'rb')
  local old = current and current:read(2049) or nil
  if current then current:close() end
  if old == text then return end
  local file = io.open(env.state_path, 'wb')
  if file then file:write(text);file:close() end
end

local function apply(env, state)
  if not state then return end
  env.syncing = true
  local context = env.engine.context
  for _, name in ipairs(names) do context:set_option(name, state[name]) end
  env.syncing = false
end

function M.init(env)
  local root = rime_api.get_user_data_dir()
  env.command_path = root .. '/.mason-modes-command.ini'
  env.state_path = root .. '/.mason-modes-state.ini'
  local command, observed = read(env.command_path), read(env.state_path)
  env.command_revision = command and command.revision or nil
  local initial = command and (observed and observed.revision == command.revision and observed or command) or observed
  apply(env, initial)
  env.connection = env.engine.context.option_update_notifier:connect(function(context, name)
    if tracked[name] then snapshot(env, context) end
  end)
  snapshot(env, env.engine.context)
end

function M.func(key, env)
  local command = read(env.command_path)
  if command and command.revision ~= env.command_revision then
    env.command_revision = command.revision
    apply(env, command)
  end
  snapshot(env, env.engine.context)
  return 2 -- kNoop: normal Rime processors handle the key after the options are set.
end

function M.fini(env)
  if env.connection then env.connection:disconnect();env.connection = nil end
end

return M
