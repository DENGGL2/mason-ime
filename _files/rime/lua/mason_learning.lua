-- Local raw-code preferences, separate from Rime's phonetic user dictionary.
-- Loaded once per service; append only after a deliberate, complete selection.
local M = {}
local stores = {}
local marker = utf8.char(0x2063)
local types = {phrase=true, user_phrase=true, sentence=true, table=true,
               mason_correction=true, mason_learned=true}

local function valid_code(code)
  return type(code) == 'string' and #code > 0 and #code <= 32
      and code:match("^[a-z][a-z']*$") and code:sub(1, 1) ~= 'v'
end

local function valid_field(value, limit)
  return type(value) == 'string' and #value <= limit
      and not value:find('[%z\1-\31\127]') and utf8.len(value) ~= nil
end

local function warn(store, message)
  if not store.reported then
    store.reported = true
    log.error('Mason selection history: ' .. tostring(message))
  end
end

local function apply(store, scope, code, text, corrected, preedit)
  local key = scope .. '\t' .. code
  local values = store.values[key]
  if not values then values = {}; store.values[key] = values end
  local entry = values[text] or {count=0, text=text}
  store.serial = store.serial + 1
  entry.count = math.min(1000000, entry.count + 1)
  entry.last, entry.corrected, entry.preedit = store.serial, corrected, preedit
  values[text] = entry
end

local function load_store(directory)
  if stores[directory] then return stores[directory] end
  local store = {path=directory .. '/mason-learning.tsv', values={}, serial=0}
  stores[directory] = store
  local file = io.open(store.path, 'rb')
  if not file then return store end
  for line in file:lines() do
    local scope, code, text, kind, preedit = line:match(
        '^v1\t([^\t]+)\t([^\t]+)\t([^\t]+)\t([nc])\t([^\t]*)\r?$')
    if scope and #scope <= 128 and scope:match('^[%w_:%-]+$')
        and valid_code(code) and text ~= '' and valid_field(text, 512)
        and valid_field(preedit, 256) then
      apply(store, scope, code, text, kind == 'c', preedit)
    end
  end
  file:close()
  return store
end

local function scope(env)
  local context = env.engine.context
  local value = env.learning_schema .. ':'
  -- Final displayed text belongs to its conversion mode; do not inject a
  -- simplified saved candidate after the user switches to traditional text.
  for _, option in ipairs({'traditionalization', 'mars', 'chinese_english', 'emoji'}) do
    value = value .. (context:get_option(option) and '1' or '0')
  end
  return value
end

function M.prepare(env)
  env.learning_schema = env.engine.schema.schema_id
  env.learning_flexible = env.engine.schema.config:get_bool('mason_learning/flexible') == true
  env.learning_correction = env.engine.schema.config:get_bool('mason_correction/enabled') == true
  env.learning_store = load_store(rime_api.get_user_data_dir())
end

local function genuine(candidate)
  return candidate:get_genuine() or candidate
end

local function full_candidate(candidate, code)
  return candidate and candidate.start == 0 and candidate._end == #code
      and types[genuine(candidate).type] and candidate.text ~= code
end

local function confirmed(context)
  local segment = context.composition:back()
  return segment and segment.status == 'kConfirmed'
end

local function record(context, env)
  local state, code = env.learning_state, context.input
  if not state or not valid_code(code) or context:get_option('ascii_mode')
      or not confirmed(context) then return end
  local intent = state.code == code and state.intent or nil
  -- No key intent means a candidate was explicitly selected by the host/mouse.
  -- Programmatic blur commits stay kGuess and are rejected above.
  if intent == 'automatic' or intent == 'literal' then return end
  local candidate = context:get_selected_candidate()
  if not full_candidate(candidate, code) or context:get_commit_text() ~= candidate.text
      or not valid_field(candidate.text, 512) then return end
  local preedit = candidate.preedit or code
  if not valid_field(preedit, 256) then preedit = code end
  local corrected = preedit:find(marker, 1, true) ~= nil
  local store, current_scope = env.learning_store, scope(env)
  apply(store, current_scope, code, candidate.text, corrected, preedit)
  state.intent = 'automatic' -- prevent a duplicate notifier from counting twice
  local file, error = io.open(store.path, 'ab')
  if not file then warn(store, error); return end
  local written, write_error = file:write('v1\t', current_scope, '\t', code, '\t',
      candidate.text, '\t', corrected and 'c' or 'n', '\t', preedit, '\n')
  local closed, close_error = file:close()
  if not written or not closed then warn(store, write_error or close_error) end
end

function M.attach(env)
  M.prepare(env)
  local context = env.engine.context
  env.learning_state = {code=context.input, navigated=false}
  env.learning_commit = context.commit_notifier:connect(function(current)
    local ok, error = pcall(record, current, env)
    if not ok then warn(env.learning_store, error) end
  end)
  env.learning_update = context.update_notifier:connect(function(current)
    local state = env.learning_state
    if current.input ~= state.code then
      state.code, state.intent, state.navigated = current.input, nil, false
    end
  end)
end

function M.detach(env)
  if env.learning_commit then env.learning_commit:disconnect() end
  if env.learning_update then env.learning_update:disconnect() end
end

function M.observe(key, env)
  if key:release() then return end
  local context, state = env.engine.context, env.learning_state
  if not state then return end
  if context.input ~= state.code then
    state.code, state.navigated = context.input, false
  end
  state.intent = nil
  if not valid_code(context.input) or not context:has_menu() then return end
  local code = key.keycode
  if code == 0xff0d or code == 0xff8d then state.intent = 'literal'; return end
  if key:ctrl() or key:alt() then return end
  if not key:shift() and ((code >= 49 and code <= 57)
      or (code >= 0xffb1 and code <= 0xffb9) or code == 59) then
    state.intent = 'explicit'
  elseif code == 32 and not key:shift() then
    state.intent = state.navigated and 'explicit' or 'automatic'
  elseif code == 0xff52 or code == 0xff54 or code == 0xff55 or code == 0xff56
      or code == 45 or code == 61 or code == 43 or code == 0xffab or code == 0xffad then
    state.navigated = true
  end
end

local function active(entry, env)
  return not entry.corrected or env.learning_correction
end

local function emit_all(input, emit)
  for candidate in input:iter() do emit(candidate) end
end

-- The existing final filter calls this function, so user filter patches are
-- untouched and no competing /@next or /@before index needs to be installed.
function M.filter(input, env, emit)
  local context, code = env.engine.context, env.engine.context.input
  local segment = context.composition:back()
  local values = valid_code(code) and not context:get_option('ascii_mode')
      and segment and segment.start == 0
      and env.learning_store.values[scope(env) .. '\t' .. code]
  if not values then emit_all(input, emit); return end

  if not env.learning_flexible then
    local preferred
    for _, entry in pairs(values) do
      if entry.count >= 2 and active(entry, env)
          and (not preferred or entry.last > preferred.last) then preferred = entry end
    end
    if not preferred then emit_all(input, emit); return end
    local buffered, first = {}, nil
    for candidate in input:iter() do
      if full_candidate(candidate, code) and candidate.text == preferred.text then
        first = candidate; break
      end
      buffered[#buffered + 1] = candidate
      if #buffered == 96 then break end
    end
    if not first then
      -- A previously confirmed whole-word choice can be beyond the lazy menu.
      -- A plain candidate prevents storing a wrong dictionary code on commit.
      first = Candidate('mason_learned', 0, #code, preferred.text, '')
      first.preedit, first.quality = preferred.preedit, 1.3
    end
    emit(first)
    local function other(candidate)
      if not (full_candidate(candidate, code) and candidate.text == preferred.text) then emit(candidate) end
    end
    for _, candidate in ipairs(buffered) do other(candidate) end
    emit_all(input, other)
    return
  end

  -- Native quality plus a bounded logarithmic frequency boost. Monotonic
  -- baseline scores preserve the current relative order of unlearned choices.
  local buffered, previous = {}, math.huge
  for candidate in input:iter() do
    local quality = candidate.quality
    if quality ~= quality or math.abs(quality) == math.huge then quality = 0 end
    local base = math.min(quality, previous - .02)
    previous = base
    local entry = full_candidate(candidate, code) and values[candidate.text]
    local count = entry and active(entry, env) and entry.count or 0
    local boost = math.min(.8, .18 * math.log(1 + count))
    buffered[#buffered + 1] = {candidate=candidate, score=base + boost, index=#buffered + 1}
    if #buffered == 64 then break end
  end
  table.sort(buffered, function(a, b)
    if a.score == b.score then return a.index < b.index end
    return a.score > b.score
  end)
  for _, value in ipairs(buffered) do emit(value.candidate) end
  emit_all(input, emit)
end

return M
