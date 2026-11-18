#pragma once
#include <windows.h>
#include <d2d1.h>
#include <dwrite.h>
#include <string>
#include <vector>

namespace MasonCorrectionText {
// Rime keeps the original input. Only the candidate preedit carries this
// invisible error marker; it must never become committed Chinese or user-dict code.
constexpr wchar_t ErrorMarker = 0x2063;
constexpr wchar_t ZeroWidth = 0x200B;
struct Plan {
  std::wstring text;
  std::vector<DWRITE_TEXT_RANGE> errors;
};
inline bool Letter(wchar_t c) {
  return (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z');
}
inline Plan Parse(const wchar_t* source, UINT32 length) {
  Plan plan;
  if (!source) return plan;
  plan.text.assign(source, length);
  for (UINT32 i = 1; i < length; ++i) {
    if (source[i] == ErrorMarker && Letter(source[i - 1])) {
      // Keep code-unit offsets used by Weasel's text selection and caret.
      // The marker is invisible even before the native colour adapter loads.
      plan.text[i] = ZeroWidth;
      plan.errors.push_back({i - 1, 1});
    }
  }
  return plan;
}
inline void Decorate(IDWriteTextLayout* layout, const Plan& plan) {
  // Colour is applied at DrawTextLayout with a brush from its render target.
  // Original text and glyph advances are preserved, without a strike line.
}
using DrawTextLayoutFunction = void(STDMETHODCALLTYPE*)(
    ID2D1RenderTarget*, D2D1_POINT_2F, IDWriteTextLayout*, ID2D1Brush*, D2D1_DRAW_TEXT_OPTIONS);
inline void Draw(ID2D1RenderTarget* target, D2D1_POINT_2F origin,
                 IDWriteTextLayout* layout, ID2D1Brush* normal,
                 const std::vector<DWRITE_TEXT_RANGE>& errors,
                 D2D1_DRAW_TEXT_OPTIONS options = D2D1_DRAW_TEXT_OPTIONS_NONE,
                 DrawTextLayoutFunction original = nullptr) {
  ID2D1SolidColorBrush* error = nullptr;
  ID2D1SolidColorBrush* solid = nullptr;
  float lightness = 0;
  if (normal && SUCCEEDED(normal->QueryInterface(__uuidof(ID2D1SolidColorBrush), reinterpret_cast<void**>(&solid)))) {
    const auto color = solid->GetColor();
    lightness = color.r * .2126f + color.g * .7152f + color.b * .0722f;
    solid->Release();
  }
  const D2D1_COLOR_F red = lightness > .5f
      ? D2D1::ColorF(0xF07E86) : D2D1::ColorF(0xD94B54);
  std::vector<IUnknown*> previous(errors.size(), nullptr);
  if (!errors.empty() && SUCCEEDED(target->CreateSolidColorBrush(red, &error))) {
    error->SetOpacity(normal ? normal->GetOpacity() : 1.f);
    for (size_t index = 0; index < errors.size(); ++index) {
      const auto& range = errors[index];
      IUnknown* effect = nullptr;
      layout->GetDrawingEffect(range.startPosition, &effect);
      previous[index] = effect;
      layout->SetDrawingEffect(error, range);
    }
  }
  if (original) original(target, origin, layout, normal, options);
  else target->DrawTextLayout(origin, layout, normal, options);
  if (error) {
    for (size_t i = 0; i < previous.size(); ++i) {
      layout->SetDrawingEffect(previous[i], errors[i]);
      if (previous[i]) previous[i]->Release();
    }
    error->Release();
  }
}
}
