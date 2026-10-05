// YAML editor for the Config tab (CodeMirror 6). Built with `npm run build` into frte2tg/web/yaml-editor.js,
// which the app serves at /js/yaml-editor.js, so the page works without internet access.
import { EditorView, basicSetup } from "codemirror";
import { EditorState } from "@codemirror/state";
import { keymap } from "@codemirror/view";
import { indentWithTab } from "@codemirror/commands";
import { indentUnit, HighlightStyle, syntaxHighlighting, StreamLanguage } from "@codemirror/language";
import { yaml, yamlLanguage } from "@codemirror/lang-yaml";
import { linter, lintGutter } from "@codemirror/lint";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { tags, highlightCode } from "@lezer/highlight";
import { StyleModule } from "style-mod";
import { parseDocument } from "yaml";

const INDENT = "  ";

// YAML forbids tabs for indentation: pasted text gets them replaced with spaces (Tab key indents via indentUnit).
const pasteWithoutTabs = EditorView.domEventHandlers({
  paste(event, view) {
    const text = event.clipboardData?.getData("text/plain");
    if (!text || !text.includes("\t")) return false;
    event.preventDefault();
    view.dispatch(view.state.replaceSelection(text.replace(/\t/g, INDENT)), { scrollIntoView: true, userEvent: "input.paste" });
    return true;
  }
});

// Syntax errors and warnings from the `yaml` parser, underlined in place.
const yamlLinter = linter(view => {
  const text = view.state.doc.toString();
  const doc = parseDocument(text, { prettyErrors: false });
  const toDiagnostic = severity => e => {
    const from = Math.min(e.pos[0], text.length);
    const to = Math.min(Math.max(e.pos[1], from + 1), text.length);
    return { from, to: Math.max(to, from), severity, message: e.message.split("\n")[0] };
  };
  return [...doc.errors.map(toDiagnostic("error")), ...doc.warnings.map(toDiagnostic("warning"))];
}, { delay: 400 });

// Colors come from the page's CSS variables, so the editor matches the dark UI.
const theme = EditorView.theme({
  "&": { height: "100%", backgroundColor: "var(--bg)", color: "var(--text)", fontSize: "13px" },
  "&.cm-focused": { outline: "none" },
  ".cm-scroller": { fontFamily: "'JetBrains Mono', monospace", lineHeight: "1.7" },
  ".cm-content": { caretColor: "var(--accent)" },
  ".cm-cursor, .cm-dropCursor": { borderLeftColor: "var(--accent)" },
  ".cm-gutters": { backgroundColor: "var(--bg2)", color: "var(--muted)", borderRight: "1px solid var(--border)" },
  ".cm-activeLine": { backgroundColor: "rgba(88, 166, 255, 0.06)" },
  ".cm-activeLineGutter": { backgroundColor: "var(--bg3)", color: "var(--text)" },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection": { backgroundColor: "rgba(88, 166, 255, 0.25) !important" },
  ".cm-panels": { backgroundColor: "var(--bg2)", color: "var(--text)", borderTop: "1px solid var(--border)" },
  ".cm-panels input, .cm-panels button": { color: "var(--text)" },
  ".cm-tooltip": { backgroundColor: "var(--bg2)", border: "1px solid var(--border)", color: "var(--text)" },
  ".cm-foldPlaceholder": { backgroundColor: "var(--bg3)", border: "none", color: "var(--muted)" }
}, { dark: true });

// Syntax colors (tokens from the YAML grammar).
const highlight = HighlightStyle.define([
  { tag: [tags.propertyName, tags.definition(tags.propertyName)], color: "#7ee787" },
  { tag: [tags.string, tags.special(tags.string), tags.content], color: "#a5d6ff" },
  { tag: [tags.number, tags.bool, tags.null, tags.atom], color: "#79c0ff" },
  { tag: tags.comment, color: "#8b949e", fontStyle: "italic" },
  { tag: [tags.keyword, tags.typeName, tags.meta, tags.labelName], color: "#ff7b72" },
  { tag: [tags.punctuation, tags.separator, tags.squareBracket, tags.brace], color: "#c9d1d9" }
]);

const shellLanguage = StreamLanguage.define(shell);
const codeLanguages = { yaml: yamlLanguage, yml: yamlLanguage, bash: shellLanguage, sh: shellLanguage, shell: shellLanguage };

// Colors the <pre><code class="language-xxx"> blocks under `root` (Markdig output) with the editor's highlight style.
export function highlightCodeBlocks(root) {
  if (highlight.module)
    StyleModule.mount(document, highlight.module);
  root.querySelectorAll("pre > code[class*='language-']").forEach(code => {
    const lang = [...code.classList].map(c => c.replace("language-", "")).find(l => codeLanguages[l]);
    if (!lang) return;
    const text = code.textContent;
    const tree = codeLanguages[lang].parser.parse(text);
    const out = document.createDocumentFragment();
    highlightCode(text, tree, highlight,
      (piece, classes) => {
        if (!classes) { out.append(piece); return; }
        const span = document.createElement("span");
        span.className = classes;
        span.textContent = piece;
        out.append(span);
      },
      () => out.append("\n"));
    code.replaceChildren(out);
  });
}

export function createYamlEditor(parent, text, onChange) {
  const view = new EditorView({
    parent,
    state: EditorState.create({
      doc: text,
      extensions: [
        basicSetup,
        yaml(),
        indentUnit.of(INDENT),
        EditorState.tabSize.of(2),
        keymap.of([indentWithTab]),
        pasteWithoutTabs,
        lintGutter(),
        yamlLinter,
        theme,
        syntaxHighlighting(highlight),
        EditorView.updateListener.of(u => { if (u.docChanged && onChange) onChange(); })
      ]
    })
  });
  return {
    getValue: () => view.state.doc.toString(),
    setValue: value => view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: value } }),
    focus: () => view.focus()
  };
}
