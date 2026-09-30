# `XmlReader.ReadElementContentAsString()` already advances the reader

- **`XmlReader.ReadElementContentAsString()` already advances the reader.** Calling
  `Read()` again after it silently skips the next sibling; that is how every `<time>` after
  an `<ele>` went missing and GPX tracks all fell back to an assumed pace.
