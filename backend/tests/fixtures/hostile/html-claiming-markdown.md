<!--
  Extension and content disagree.

  This file claims to be Markdown and is HTML. FR-007 says the content type is determined from
  validated content rather than from the claimed extension, and the "extension does not match
  content" edge case says the disagreement itself is the finding.

  It matters because Markdown is rendered with raw HTML disabled, so a file accepted *as*
  Markdown on the strength of its name would take the safe path while containing exactly the
  content that path is not designed to neutralise.

  Expectation: the upload validator refuses this file and names the disagreement.
-->
<!doctype html>
<html>
  <head>
    <title>Definitely markdown</title>
  </head>
  <body>
    <script>
      alert('accepted as markdown, rendered as html');
    </script>
    <h1>Not Markdown at all</h1>
    <p>If this renders as HTML from a .md upload, content sniffing failed.</p>
  </body>
</html>
