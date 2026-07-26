# Markdown with embedded HTML

Markdown is rendered with raw HTML disabled, so everything below must appear as literal text
rather than as markup. Expectation: no element here reaches the render artifact.

<script>alert('markdown inline script')</script>

<img src="https://evil.example.com/pixel.gif" onerror="alert('markdown img')" alt="" />

<div onclick="alert('markdown div')">Clickable</div>

<iframe src="https://evil.example.com/frame.html"></iframe>

An ordinary autolink, which is fine: <https://example.com>

A link with a javascript scheme, which is not: [click me](javascript:alert('markdown link'))

A link with a data scheme: [data](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)

An image with a javascript scheme: ![alt](javascript:alert('markdown image'))

A reference link that resolves to a hostile scheme:

[reference][evil]

[evil]: javascript:alert('markdown reference')

<!-- An HTML comment, which must not be able to unbalance the document. -->

Text after everything, which must survive.
