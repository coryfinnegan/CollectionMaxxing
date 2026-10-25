// Local design-ratchet rules (#307): the eslint-plugin-react-native trio
// (no-color-literals, no-inline-styles, no-raw-text) plus the Phase 0 banned
// patterns (doubled titles, gradients), with zero new dependencies. Run by
// eslint.design.config.js through scripts/design-gate.mjs.
'use strict';

const colorPatterns = [/^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/, /^rgba?\s*\(/i, /^hsla?\s*\(/i];

const isColorLiteral = (value) => typeof value === 'string' && colorPatterns.some((re) => re.test(value.trim()));

const noRawColors = {
  meta: { type: 'problem', docs: { description: 'disallow raw color literals outside tokens.json' } },
  create(context) {
    const check = (node, value) => {
      if (isColorLiteral(value)) {
        context.report({ node, message: `Raw color ${JSON.stringify(value)} is banned; use a useColors() role or fixed value from tokens.json.` });
      }
    };
    return {
      Literal(node) {
        check(node, node.value);
      },
      TemplateLiteral(node) {
        if (node.expressions.length === 0 && node.quasis.length === 1) check(node, node.quasis[0].value.cooked);
      },
    };
  },
};

const styleProps = new Set(['style', 'contentContainerStyle']);

// A style value is static when it is a literal or a literal in disguise: an
// expression-free template, a unary expression over a static value, an
// array or object holding a static value, or a TS as/satisfies cast of one.
// Anything else — an identifier, a member expression like colors.ground, a
// call, a conditional — is dynamic: a module-scope StyleSheet cannot
// express it (#331).
function isStaticStyleValue(node) {
  if (!node) return false;
  if (node.type === 'Literal') return true;
  if (node.type === 'TemplateLiteral') return node.expressions.length === 0;
  if (node.type === 'UnaryExpression') return isStaticStyleValue(node.argument);
  if (node.type === 'ArrayExpression') return node.elements.some(isStaticStyleValue);
  if (node.type === 'ObjectExpression') return inlineObjectHasStaticProp(node);
  if (node.type === 'TSAsExpression' || node.type === 'TSSatisfiesExpression' || node.type === 'TSTypeAssertion') {
    return isStaticStyleValue(node.expression);
  }
  return false;
}

/** True when the inline object holds anything a module-scope StyleSheet could hold. */
function inlineObjectHasStaticProp(node) {
  return node.properties.some((prop) => prop.type === 'Property' && isStaticStyleValue(prop.value));
}

function* inlineStyleObjects(value) {
  if (!value || value.type !== 'JSXExpressionContainer') return;
  const expr = value.expression;
  if (expr.type === 'ObjectExpression') {
    yield expr;
  } else if (expr.type === 'ArrayExpression') {
    for (const el of expr.elements) if (el && el.type === 'ObjectExpression') yield el;
  } else if ((expr.type === 'ArrowFunctionExpression' || expr.type === 'FunctionExpression') && expr.body && expr.body.type !== 'BlockStatement') {
    const body = expr.body;
    if (body.type === 'ObjectExpression') {
      yield body;
    } else if (body.type === 'ArrayExpression') {
      for (const el of body.elements) if (el && el.type === 'ObjectExpression') yield el;
    }
  }
}

const noInlineStyles = {
  meta: { type: 'problem', docs: { description: 'disallow statically-valued inline style objects in new files' } },
  create(context) {
    return {
      JSXAttribute(node) {
        if (node.name.type !== 'JSXIdentifier' || !styleProps.has(node.name.name)) return;
        for (const obj of inlineStyleObjects(node.value)) {
          if (inlineObjectHasStaticProp(obj)) {
            context.report({ node, message: 'Inline styles with static values are banned in new files; move them to StyleSheet and keep only dynamic values inline.' });
            return;
          }
        }
      },
    };
  },
};

function elementName(node) {
  if (!node || node.type !== 'JSXElement') return null;
  const opening = node.openingElement.name;
  if (opening.type === 'JSXIdentifier') return opening.name;
  if (opening.type === 'JSXMemberExpression') {
    const parts = [];
    let current = opening;
    while (current && current.type === 'JSXMemberExpression') {
      parts.unshift(current.property.type === 'JSXIdentifier' ? current.property.name : null);
      current = current.object;
    }
    if (current && current.type === 'JSXIdentifier') parts.unshift(current.name);
    if (parts.every(Boolean)) return parts.join('.');
  }
  return null;
}

const noRawText = {
  meta: { type: 'problem', docs: { description: 'disallow raw text outside Text components' }, schema: [{ type: 'object', properties: { skip: { type: 'array', items: { type: 'string' } } }, additionalProperties: false }] },
  create(context) {
    // Text hosts are components designed to take text children: the Text
    // primitive plus the native tab trigger label (a framework host like Text).
    const skip = new Set(['Text', 'NativeTabs.Trigger.Label', ...((context.options[0] && context.options[0].skip) || [])]);
    return {
      ImportDeclaration(node) {
        for (const spec of node.specifiers) {
          if (spec.type === 'ImportSpecifier' && spec.imported.name === 'Text') skip.add(spec.local.name);
        }
      },
      'JSXElement > JSXText'(node) {
        if (node.value.trim() === '') return;
        let parent = node.parent;
        while (parent && parent.type === 'JSXFragment') parent = parent.parent;
        if (!skip.has(elementName(parent))) {
          context.report({ node, message: 'Raw text outside a Text component is banned; use the Text primitive.' });
        }
      },
      'JSXElement > JSXExpressionContainer'(node) {
        const expr = node.expression;
        if (!expr || expr.type !== 'Literal' || typeof expr.value !== 'string' || expr.value.trim() === '') return;
        let parent = node.parent;
        while (parent && parent.type === 'JSXFragment') parent = parent.parent;
        if (!skip.has(elementName(parent))) {
          context.report({ node, message: 'Raw text outside a Text component is banned; use the Text primitive.' });
        }
      },
    };
  },
};

function literalString(node) {
  if (!node) return null;
  if (node.type === 'Literal' && typeof node.value === 'string') return node.value;
  if (node.type === 'JSXExpressionContainer') return literalString(node.expression);
  return null;
}

function jsxAttribute(node, name) {
  return node.openingElement.attributes.find((a) => a.type === 'JSXAttribute' && a.name.name === name) || null;
}

const noDoubledTitle = {
  meta: { type: 'problem', docs: { description: 'disallow an in-screen title repeating the navigator header' } },
  create(context) {
    const navTitles = new Set();
    const inScreen = [];
    return {
      JSXAttribute(node) {
        if (node.name.type !== 'JSXIdentifier' || node.name.name !== 'options') return;
        const options = node.value && node.value.type === 'JSXExpressionContainer' ? node.value.expression : null;
        if (!options || options.type !== 'ObjectExpression') return;
        for (const prop of options.properties) {
          if (prop.type !== 'Property' || prop.key.type !== 'Identifier') continue;
          if ((prop.key.name === 'title' || prop.key.name === 'headerTitle') && prop.value.type === 'Literal' && typeof prop.value.value === 'string') {
            navTitles.add(prop.value.value.trim());
          }
        }
      },
      JSXElement(node) {
        const name = elementName(node);
        if (name === 'Screen') {
          const title = literalString((jsxAttribute(node, 'title') || {}).value);
          if (title) inScreen.push({ node, title: title.trim() });
        } else if (name === 'Text') {
          const variant = literalString((jsxAttribute(node, 'variant') || {}).value);
          if (variant === 'title' || variant === 'display') {
            const text = node.children
              .map((child) => {
                if (child.type === 'JSXText') return child.value;
                if (child.type === 'JSXExpressionContainer') return literalString(child) || '';
                return '';
              })
              .join('')
              .trim();
            if (text) inScreen.push({ node, title: text });
          }
        }
      },
      'Program:exit'() {
        for (const { node, title } of inScreen) {
          if (navTitles.has(title)) {
            context.report({ node, message: `Doubled title ${JSON.stringify(title)}: the navigator header already names this screen.` });
          }
        }
      },
    };
  },
};

const noGradients = {
  meta: { type: 'problem', docs: { description: 'disallow gradient components' } },
  create(context) {
    return {
      ImportDeclaration(node) {
        if (typeof node.source.value === 'string' && node.source.value.toLowerCase().includes('gradient')) {
          context.report({ node, message: 'Gradients are banned; the design system has no gradient library.' });
        }
      },
      JSXOpeningElement(node) {
        if (node.name.type === 'JSXIdentifier' && node.name.name.toLowerCase().includes('gradient')) {
          context.report({ node, message: 'Gradients are banned; the design system has no gradient library.' });
        }
      },
    };
  },
};

module.exports = { rules: { 'no-raw-colors': noRawColors, 'no-inline-styles': noInlineStyles, 'no-raw-text': noRawText, 'no-doubled-title': noDoubledTitle, 'no-gradients': noGradients } };
