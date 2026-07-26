# Feature Specification: BlinkMark Core

**Feature Branch**: `001-blinkmark-core`  
**Created**: 2026-07-26  
**Status**: Draft  
**Input**: User description: "BlinkMark — file upload (HTML/Markdown), anchored commenting, 24-hour default retention extendable to 30 days, email/Teams notifications, tenant-only secure access, integrated live preview. Non-functional: encrypted storage, strict Azure AD authorization, fast upload/retrieval and low-latency commenting, hundreds of concurrent users, enforced retention with audit logs, intuitive UI. Additionally: the app must be AI-ready so an AI agent can access it on behalf of the user; and the app must show who and how many users are currently online on a document."

## Clarifications

### Session 2026-07-26

- Q: Who can read the audit trail, and how? → A: No in-product access — audit entries are retained and retrievable only by operators through platform tooling; operational documentation and runbooks are out of scope for this phase.
- Q: Can users download a file, given FR-041 audits "download" but nothing grants it? → A: Yes, but the file's owner only. A download includes the file's comments alongside the content.
- Q: What availability and durability posture should the service hold? → A: Best-effort — single region, locally redundant, no disaster recovery. Loss of files and comments in an infrastructure failure is an accepted outcome.
- Q: What accessibility standard applies? → A: WCAG 2.1 Level AA across all user-facing flows, including keyboard-only commenting and screen-reader announcement of presence changes.
- Q: What limits constrain human users, given only agents are rate-limited? → A: A per-user cap on simultaneously live files, plus a per-user upload rate limit.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Share a draft and preview it safely (Priority: P1) 🎯 MVP

A person has an HTML or Markdown draft — a report, a design doc, a generated page — that is not ready to publish. They upload it to BlinkMark and get a link. Anyone in their organization who opens that link signs in and sees the document rendered live in the browser, exactly as it will look, without downloading anything or installing a viewer. Twenty-four hours later the file is gone on its own, so nobody has to remember to clean it up.

**Why this priority**: This is the smallest slice that delivers real value. Upload, secure viewing, and automatic disappearance are the entire reason the product exists — a person can stop emailing zip files of HTML around. Commenting is worthless without something to comment on.

**Independent Test**: Upload an HTML file and a Markdown file, open the returned link in a second authenticated browser session, confirm both render correctly, then confirm the file is inaccessible after its expiry time passes and that an unauthenticated session is refused at every step.

**Acceptance Scenarios**:

1. **Given** an authenticated tenant user, **When** they upload a valid `.html` file within the size limit, **Then** the system stores it, sets its expiry to 24 hours from upload, and returns a shareable link.
2. **Given** an authenticated tenant user, **When** they upload a valid `.md` file, **Then** the system renders it as formatted HTML in the preview.
3. **Given** a file link, **When** an unauthenticated visitor opens it, **Then** they are prompted to sign in and see no file content, metadata, or filename before authenticating.
4. **Given** a file link forwarded to a colleague the uploader never named, **When** that colleague authenticates as a member of the organization, **Then** they can view and comment on the file.
5. **Given** a file link, **When** a person outside the tenant authenticates successfully with their own organization's account, **Then** access is refused.
6. **Given** an uploaded HTML file containing an embedded script, **When** any user previews it, **Then** the script does not execute and cannot read the viewer's session.
7. **Given** a file whose expiry time has passed, **When** anyone opens its link, **Then** the system reports the file as expired and returns no content.
8. **Given** an upload that exceeds the size limit or is not a supported type, **When** the user submits it, **Then** the system rejects it before storing and explains why.

---

### User Story 2 - Comment on a specific passage (Priority: P2)

A reviewer opens a shared draft, highlights a sentence that reads badly or draws a box around a chart that looks wrong, and types a comment. The comment stays visibly pinned to that exact passage. Other reviewers open the same link and see the highlight and the comment in place, and can reply. Anchors are matched against the document's content rather than its layout, so they survive re-rendering; and on the rare occasion an anchor cannot be found at all, the comment is shown separately as orphaned with the original quoted passage preserved, so nothing is silently lost.

**Why this priority**: This is the differentiating capability, but it depends entirely on US1 being in place. It converts BlinkMark from a file viewer into a review tool.

**Independent Test**: Open a shared file, select a text range, add a comment, reload in a different session and confirm the comment appears anchored to the same text; then present the same comment against a render in which its passage cannot be located, and confirm it surfaces as orphaned with its quoted context rather than disappearing or moving.

**Acceptance Scenarios**:

1. **Given** a previewed file, **When** a user selects a text range and submits a comment, **Then** the comment is saved with an anchor derived from the selected content and appears highlighted in place.
2. **Given** a previewed file, **When** a user drags a region selection over a non-text area, **Then** a comment can be attached to that region.
3. **Given** an existing comment, **When** a second user opens the same file, **Then** they see the comment at the same anchor position and can reply to it.
4. **Given** a comment whose anchored passage cannot be located in the current render of the file, **When** the file is previewed, **Then** the comment is listed as orphaned together with the original quoted text, and is not attached to any other passage.
5. **Given** a comment submission, **When** the client supplies an author identity different from the signed-in user, **Then** the system records the signed-in user as author and ignores the supplied value.
6. **Given** comment text containing markup, **When** it is displayed, **Then** it renders as literal text and does not execute.
7. **Given** a file that is deleted or expires, **When** the deletion completes, **Then** all comments on that file are removed with it.

---

### User Story 3 - Keep a file alive longer (Priority: P3)

A review is taking longer than a day. The person who uploaded the file sees how much time is left before it disappears and extends it — another few days, or out to the maximum of thirty days from when it was first uploaded. They cannot extend it indefinitely, and nobody else can extend their file.

**Why this priority**: Without this, the 24-hour default makes the product unusable for any review that spans a weekend. But the product is still demonstrable and useful without it, so it follows the core loop.

**Independent Test**: Upload a file, confirm the displayed expiry is 24 hours out, extend it, confirm the new expiry is reflected and enforced, then attempt to extend beyond thirty days from upload and confirm refusal.

**Acceptance Scenarios**:

1. **Given** an uploaded file, **When** its owner views it, **Then** the remaining time before automatic deletion is displayed.
2. **Given** a file owned by the current user, **When** they extend retention to a date within thirty days of the original upload, **Then** the new expiry is applied and confirmed.
3. **Given** a file owned by the current user, **When** they request an expiry beyond thirty days from original upload, **Then** the request is refused and the existing expiry is unchanged.
4. **Given** a file owned by someone else, **When** a different user attempts to extend it, **Then** the request is refused.
5. **Given** any successful retention extension, **When** it completes, **Then** an audit record captures who extended it, the file, the previous expiry, and the new expiry.
6. **Given** a file that reaches its expiry, **When** the expiry passes, **Then** the file content, its metadata, and its comments are removed without any user action.
7. **Given** a file owned by the current user, **When** they download it, **Then** they receive the content together with all of its comments — including orphaned ones — each showing its author, time, and the passage it was anchored to.
8. **Given** a file owned by someone else, **When** a user who is only a viewer attempts to download it, **Then** the request is refused.
9. **Given** an owner downloading their file, **When** the download is offered, **Then** they are told the downloaded copy will outlive the file's expiry.

---

### User Story 4 - Find out when someone comments (Priority: P4)

The person who shared a file does not sit watching it. When a reviewer leaves a comment, the uploader gets a notification by email or in Teams telling them who commented on which file, with a link straight to it. People who have already commented on the file hear about replies too. Nobody is forced to receive a flood — notifications for a burst of activity arrive together rather than one per comment.

**Why this priority**: It closes the review loop and drives return visits, but every scenario in US1–US3 works without it. It is also the piece most safely deferred, since reviewers can be told about comments out of band.

**Independent Test**: Have a second user comment on a file, and confirm the uploader receives a notification containing the commenter, the file, and a working link — while confirming the commenter's own action completed immediately regardless of notification delivery.

**Acceptance Scenarios**:

1. **Given** a file with an uploader and a commenter, **When** a new comment is created, **Then** the uploader is notified with the commenter's name, the file name, and a link to the comment.
2. **Given** a user who previously commented on a file, **When** someone replies in that thread, **Then** that user is notified.
3. **Given** a user who authored a comment, **When** the notification is sent, **Then** that user is not notified about their own action.
4. **Given** a failure in the notification channel, **When** a comment is created, **Then** the comment is still saved successfully and the user sees no error.
5. **Given** several comments added to one file in quick succession, **When** notifications are produced, **Then** the recipient receives a consolidated notification rather than one message per comment.
6. **Given** a user's notification preference set to off, **When** activity occurs on their file, **Then** no notification is sent to them.

---

### User Story 5 - Let an AI agent work on the file for me (Priority: P4)

A person is working with an AI assistant and wants it to help with a review. They ask it to pull up the draft they shared, summarize the open comments, point out which sections nobody has looked at, and leave a comment on the paragraph that contradicts an earlier section. The agent does all of this through BlinkMark acting as that person — it can reach exactly the files that person can reach and nothing more, every comment it leaves is visibly attributed to the agent acting for that person, and the person can see in the audit trail exactly what the agent did. If the person's access is revoked, the agent's access dies with it.

**Why this priority**: It is genuinely valuable and increasingly expected, but it is an access channel over capabilities defined in US1–US3. Those must exist and be correct first, or the agent has nothing safe to call.

**Independent Test**: Using an agent credential issued for a specific user, list that user's files, read one file's content and comments, and create a comment — then verify the agent cannot reach a file that user cannot reach, that the created comment is attributed to the user with the agent named as the acting client, and that every agent action appears in the audit trail marked as agent-initiated.

**Acceptance Scenarios**:

1. **Given** an AI agent holding a delegated credential for a signed-in user, **When** it requests that user's file list, **Then** it receives exactly the files that user could see through the UI.
2. **Given** an AI agent acting for user A, **When** it requests a file that user A is not permitted to see, **Then** the request is refused identically to a refusal for user A directly.
3. **Given** an AI agent acting for a user, **When** it creates a comment, **Then** the comment records the user as author and the agent as the acting client, and this attribution is visible to human readers of the comment.
4. **Given** any agent-initiated action, **When** it completes, **Then** the audit record identifies both the user and the agent, and marks the action as agent-initiated.
5. **Given** an AI agent, **When** the represented user is not currently signed in, **Then** the agent cannot act for that user and the request is refused.
6. **Given** a user whose access is revoked or whose consent is withdrawn, **When** the agent next attempts an action for that user, **Then** the action is refused.
7. **Given** an AI agent, **When** it retrieves a file's content, **Then** it receives a structured, machine-readable representation of the content and its comment anchors rather than a rendered visual page.
8. **Given** a published description of the available agent operations, **When** an agent inspects it, **Then** the operations, their inputs, and their permission requirements are discoverable without human documentation.

---

### User Story 6 - Know who else is in the document right now (Priority: P4)

A reviewer opens a shared draft and immediately sees that three colleagues are reading it too, with their names and pictures along the top. They can tell at a glance whether they are first to the document or the last to arrive, and whether the author is currently in there watching comments land. When someone leaves, they quietly disappear from the list. Nobody has to ask "is anyone else looking at this?" in a side channel.

**Why this priority**: It changes review from a set of isolated visits into something that feels shared, and it stops duplicated effort — two people writing the same comment at the same moment. But every other story works perfectly without it, so it is a peer of the other awareness features rather than a prerequisite for anything.

**Independent Test**: Open one file in two authenticated sessions and confirm each session sees the other appear, sees an accurate count, and sees the other disappear shortly after that session closes — including when the second session is terminated abruptly rather than closed cleanly.

**Acceptance Scenarios**:

1. **Given** a user viewing a file, **When** a second authorized user opens the same file, **Then** the first user sees the viewer count increase and the second user identified, without reloading the page.
2. **Given** two users viewing a file, **When** one navigates away or closes the file, **Then** the other sees them removed from the viewer list within the staleness window.
3. **Given** a viewer whose connection drops without a clean exit, **When** the staleness window elapses, **Then** they are removed from the viewer list automatically.
4. **Given** one user with the same file open in several tabs or on several devices, **When** presence is displayed, **Then** that person is represented once, not once per tab.
5. **Given** an AI agent reading a file for a user, **When** presence is displayed, **Then** the represented user is shown and marked as agent-assisted rather than appearing as an ordinary human viewer.
6. **Given** more concurrent viewers than can be displayed at once, **When** presence is shown, **Then** a subset is displayed with an accurate count of the remainder.
7. **Given** a user who is not authorized to view a file, **When** they attempt to observe its presence information, **Then** nothing about who is viewing it is disclosed.
8. **Given** viewers present on a file, **When** that file expires or is deleted, **Then** presence for it is cleared along with the file.

---

### Edge Cases

- A file is uploaded with a name that collides with an existing file, or with a name containing path separators or characters intended to escape the storage location.
- A Markdown file references images or stylesheets that do not exist, or that point at external internet addresses.
- An HTML file attempts to load remote resources, open pop-ups, or frame-bust out of the preview.
- Two users comment on overlapping text ranges at the same moment.
- A user selects text that spans the boundary of two structural elements, or selects zero-width/whitespace-only content.
- The same passage of text appears many times in a document, so an anchor's quoted text is ambiguous.
- A user extends retention on a file at the same moment the expiry process is deleting it.
- A user's session expires while they are composing a long comment.
- A file link is forwarded well beyond the audience the uploader intended, or posted in a broadly-visible channel.
- A viewer's device sleeps, loses network, or is force-quit without ever signalling departure.
- A file is opened by far more simultaneous viewers than the presence display was designed to show.
- A user is actively present on a file at the exact moment it expires.
- A file expires while a reviewer has it open in the preview.
- A user reaches their live-file cap and needs capacity immediately, with no administrator available to raise it.
- An agent uploads on a user's behalf while that user is already at their cap.
- A very large file, or one with tens of thousands of comments, is opened.
- An AI agent issues a rapid burst of requests, or requests a file that expired between its listing call and its read call.
- An uploaded file's extension does not match its actual content.
- The uploader leaves the organization while their files are still live.

## Requirements *(mandatory)*

### Functional Requirements

#### Access and identity

- **FR-001**: System MUST require successful authentication against the owning organization's directory before returning any file content, file metadata, comment, or filename.
- **FR-002**: System MUST refuse access to any identity outside the owning organization, including successfully authenticated identities from other organizations.
- **FR-003**: System MUST evaluate authorization on the server for every request; hiding an action in the interface MUST NOT be the only control preventing it.
- **FR-004**: System MUST NOT expose any means of viewing or commenting on a file without authentication, including links intended to be shared externally.
- **FR-005**: System MUST determine the acting user's identity from the authenticated session and MUST NOT accept a user identity supplied by the caller.

#### File upload

- **FR-006**: Users MUST be able to upload files of the supported document types (HTML and Markdown).
- **FR-007**: System MUST validate a file's declared type, extension, and size before storing it, and MUST reject anything that fails validation with a message explaining the reason.
- **FR-008**: System MUST store uploaded content under a system-generated identifier and MUST NOT use the caller-supplied filename or path as a storage location.
- **FR-009**: System MUST retain the original filename for display purposes only.
- **FR-010**: System MUST encrypt uploaded content at rest and in transit.
- **FR-011**: Users MUST be able to see the files they have uploaded that have not yet expired.

#### Preview

- **FR-012**: System MUST render uploaded HTML and Markdown as a live visual preview in the browser without requiring download.
- **FR-013**: System MUST prevent any script contained in uploaded content from executing.
- **FR-014**: System MUST render preview content in an isolated context that cannot read the viewer's session, credentials, or the surrounding application.
- **FR-015**: System MUST render Markdown to formatted output including headings, lists, tables, code blocks, and links.
- **FR-016**: System MUST neutralize references from uploaded content to external network resources, or render them without granting the content access to the viewer's identity.

#### Commenting

- **FR-017**: Users MUST be able to select a range of text in the preview and attach a comment to it.
- **FR-018**: Users MUST be able to select a region of the page and attach a comment to it.
- **FR-019**: System MUST persist each comment together with an anchor derived from the content it was attached to, and with the quoted text or captured context at the time of creation.
- **FR-020**: System MUST display existing comments to every user permitted to view the file, positioned at their anchors.
- **FR-021**: Users MUST be able to reply to an existing comment.
- **FR-022**: System MUST present any comment whose anchor cannot be located as orphaned, together with its original quoted context, and MUST NOT attach it to a different location and MUST NOT hide it.
- **FR-023**: System MUST record the authenticated user as the comment author.
- **FR-024**: System MUST display comment text as literal text, never as executable or structural markup.
- **FR-025**: Users MUST be able to delete or edit their own comments; the system MUST record such changes in the audit trail.

#### Retention

- **FR-026**: System MUST set every uploaded file to be deleted 24 hours after upload by default.
- **FR-027**: System MUST allow the file's owner to extend its retention up to a maximum of 30 days from the original upload time.
- **FR-028**: System MUST refuse any retention beyond 30 days from original upload, through every available interface.
- **FR-029**: System MUST allow only the file's owner to change that file's retention.
- **FR-030**: System MUST display the remaining time before deletion to users who can see the file.
- **FR-031**: System MUST delete the file content, its metadata, and all of its comments when the expiry is reached, without any user action.
- **FR-032**: System MUST treat content whose expiry has passed but which has not yet been physically removed as already deleted for every read request.
- **FR-033**: Users MUST be able to delete their own file before its expiry.

#### Notifications

- **FR-034**: System MUST notify the file owner when a new comment is added to their file.
- **FR-035**: System MUST notify participants of a comment thread when a reply is added to it.
- **FR-036**: System MUST NOT notify a user about their own action.
- **FR-037**: System MUST deliver notifications asynchronously so that a notification failure or delay never blocks, delays, or fails the user action that triggered it.
- **FR-038**: System MUST consolidate multiple notifications generated for one recipient about one file within a short window into a single message.
- **FR-039**: Notifications MUST identify the actor, the file, and include a direct link to the relevant comment.
- **FR-040**: Users MUST be able to turn off notifications directed to them.

#### Audit and compliance

- **FR-041**: System MUST record an audit entry for every upload, view, preview, download, comment creation, comment edit, comment deletion, retention change, and deletion.
- **FR-042**: Each audit entry MUST identify the actor, the action, the target file or comment, the time in a consistent time standard, and the outcome.
- **FR-043**: System MUST retain audit entries independently of the file they describe, so that deleting or expiring a file does not remove its history.
- **FR-044**: System MUST make audit entries append-only, with no interface that permits application-level modification or deletion.
- **FR-045**: System MUST exclude file content, comment text, and credentials from operational logs and telemetry.

#### AI agent access

- **FR-046**: System MUST allow an AI agent to act on behalf of a specific authenticated user, with the agent's permissions being exactly the permissions of that user and never broader.
- **FR-047**: System MUST refuse an agent request for anything the represented user could not access, with the same outcome as refusing that user directly.
- **FR-048**: System MUST attribute agent-performed actions to the represented user while also identifying the agent as the acting client, and MUST make that attribution visible to human readers.
- **FR-049**: System MUST record agent-initiated actions in the audit trail with both the represented user and the agent identified, distinguishable from actions the user performed directly.
- **FR-050**: System MUST revoke an agent's ability to act for a user immediately when that user's access or consent is withdrawn.
- **FR-051**: System MUST expose file content, comment content, and comment anchors to agents in a structured, machine-readable form suitable for processing without visual rendering.
- **FR-052**: System MUST publish a machine-readable description of the operations available to agents, including their inputs, outputs, and required permissions, so agents can discover capabilities without human documentation.
- **FR-053**: System MUST apply the same retention, expiry, and authorization rules to agent access as to human access, with no agent-only exemptions.
- **FR-054**: System MUST limit the rate of agent requests so that agent activity cannot degrade service for interactive users.
- **FR-055**: System MUST permit an agent to act only within an active delegated session for a signed-in user. The system MUST NOT provide a standing agent identity with independent access to files, and MUST NOT allow an agent to act for a user who is not currently signed in.

#### Scope of access to a file

- **FR-056**: System MUST permit any authenticated user in the owning organization who holds a file's link to view it and comment on it, without the owner granting access to them individually.
- **FR-057**: System MUST make it clear to the uploader at the point of upload that anyone in the organization who receives the link will be able to open and comment on the file.
- **FR-058**: System MUST allow the file's owner, and only the owner, to change the file's retention or delete it, regardless of who else can view it.

#### Presence and live activity

- **FR-059**: System MUST display the number of users currently viewing a file to everyone authorized to view that file.
- **FR-060**: System MUST identify the current viewers by name so users can tell who is present, not merely how many.
- **FR-061**: System MUST reflect a user joining or leaving a file in other viewers' displays without requiring them to reload.
- **FR-062**: System MUST remove a viewer from presence automatically when their session ends, whether they leave cleanly or their connection drops without notice.
- **FR-063**: System MUST represent a person once regardless of how many tabs, windows, or devices they have the file open in.
- **FR-064**: System MUST disclose presence information only to users authorized to view that file.
- **FR-065**: System MUST show a viewer who is present by way of an AI agent as the represented user, marked as agent-assisted and distinguishable from a directly present human.
- **FR-066**: System MUST display an accurate total count when there are more viewers than can be shown individually, naming at most eight and summarizing the remainder.
- **FR-067**: System MUST treat presence as transient state that is never persisted beyond the viewing session and is discarded when the file is deleted or expires.
- **FR-068**: System MUST NOT rely on presence as the record of who accessed a file; the audit trail remains the authoritative record.
- **FR-069**: System MUST continue to serve the file preview and commenting normally if presence information is unavailable or degraded.

#### Audit access

- **FR-070**: System MUST keep audit entries retrievable by operators through platform tooling, independently of the product interface. The product MUST NOT expose any interface for reading, querying, or exporting audit entries in this phase, and no user role grants such access.

#### Download

- **FR-071**: System MUST allow a file's owner to download that file.
- **FR-072**: System MUST refuse download to anyone who is not the file's owner, including an agent acting for a non-owner. Viewers and commenters have preview access only.
- **FR-073**: A download MUST include the file's comments together with its content, readable outside the product, with each comment's author, time, thread structure, and the passage it was anchored to. Comments the author deleted are excluded.
- **FR-074**: A download MUST include orphaned comments with their original quoted context, so that no comment is lost from the downloaded record.
- **FR-075**: System MUST make clear to the owner at download time that the downloaded copy is no longer governed by the file's expiry.
- **FR-076**: System MUST set the expectation in the interface that BlinkMark is temporary working space rather than a system of record, and that content is not backed up or recoverable once lost, deleted, or expired.

#### Accessibility

- **FR-077**: All user-facing flows MUST conform to WCAG 2.1 Level AA.
- **FR-078**: Users MUST be able to select a passage and attach a comment to it using the keyboard alone, without a pointing device.
- **FR-079**: System MUST convey a comment's presence, its anchored passage, and its orphaned state to assistive technology, not by visual highlight alone.
- **FR-080**: System MUST announce presence changes to assistive technology non-disruptively, without moving focus or interrupting the user's current task.
- **FR-081**: System MUST keep the preview navigable by keyboard, including a reliable way to move focus into and back out of the previewed content.
- **FR-082**: System MUST provide a non-dragging alternative for any region selection, so that attaching a comment never requires a drag gesture.

#### Usage limits

- **FR-083**: System MUST cap the number of simultaneously live, unexpired files a single user may own.
- **FR-084**: System MUST refuse an upload that would exceed that cap, and MUST tell the user their current usage and that capacity is restored by deleting a file or letting one expire.
- **FR-085**: System MUST limit the rate at which a single user may upload files.
- **FR-086**: System MUST show users their current live-file usage against the cap before they reach it.
- **FR-087**: System MUST count any upload performed by an agent on a user's behalf against that user's cap and rate limit, so delegation cannot be used to exceed a personal limit. Agents cannot upload in this phase, so this requirement governs any future interface that grants them that ability.
- **FR-088**: System MUST restore capacity automatically as files expire, with no administrative intervention required to unblock a user.

### Key Entities

- **File**: An uploaded HTML or Markdown document. Carries its original display name, content type, size, owner, upload time, current expiry time, and the absolute latest expiry it may ever be given. Owns its comments; when it goes, they go.
- **Comment**: A note left by a user on a file, holding its text, author, creation time, the anchor describing where it attaches, the quoted context captured when it was created, its current resolution state (anchored or orphaned), and — when created by an agent — the agent that acted.
- **Comment Thread**: An ordered set of replies rooted at an original comment, sharing that comment's anchor and defining who is notified of new activity.
- **Anchor**: The durable description of where a comment attaches, derived from the content itself — a text quote with its surrounding context, or a region selector — rather than from a screen position.
- **User**: A person from the owning organization, identified by the directory. Owns files, authors comments, and holds notification preferences.
- **Agent Session**: A representation of an AI agent acting for a specific user, carrying the agent's identity, the represented user, and the granted permissions. Cannot outlive the user's consent.
- **Audit Entry**: An immutable record of one action: who, what, which target, when, the outcome, and whether an agent was acting. Outlives the file it refers to.
- **Presence Session**: A live indication that one person currently has a file open — the user, the file, when they arrived, when they were last confirmed present, and whether they are present by way of an agent. Purely transient: it never becomes history, and it disappears with the session or the file.
- **Notification**: A pending or delivered message to a user about activity on a file, with its recipient, trigger, delivery channel, and state.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A first-time user can upload a file and produce a working shareable link in under 60 seconds without reading instructions.
- **SC-002**: 95% of file previews become readable within 1 second of opening the link.
- **SC-003**: 95% of comment creations and comment list loads complete within 300 milliseconds, so commenting feels conversational rather than transactional.
- **SC-004**: 95% of uploads at the maximum supported size are acknowledged within 2 seconds.
- **SC-005**: The system sustains 500 concurrent active users while continuing to meet SC-002, SC-003, and SC-004.
- **SC-006**: 100% of files reach deletion no later than their stated expiry time, verified across a sustained sample; zero files persist past 30 days from upload.
- **SC-007**: Zero unauthenticated requests and zero out-of-organization requests return file content, comment content, or metadata, verified by continuous automated probing.
- **SC-008**: Zero instances of script contained in uploaded content executing in a viewer's browser, verified against a maintained corpus of hostile sample files.
- **SC-009**: 100% of auditable actions produce a retrievable audit entry, and 100% of those entries remain retrievable after the file they describe has been deleted.
- **SC-010**: Zero comments are lost or silently relocated when file content changes; every comment is either correctly anchored or explicitly marked orphaned with its original quoted context.
- **SC-011**: 90% of reviewers successfully attach a comment to their intended passage on the first attempt.
- **SC-012**: File owners are notified of new comment activity within 5 minutes for 95% of events, and no user action ever fails because of a notification problem.
- **SC-013**: An AI agent can discover the available operations and complete a read-file-then-comment task for its user without any human-authored integration documentation.
- **SC-014**: Zero cases of an agent accessing content its represented user could not access, and zero cases of an agent acting for a user with no active session, verified by automated authorization probing.
- **SC-015**: 100% of agent-initiated actions are distinguishable from direct user actions in the audit trail.
- **SC-016**: 100% of uploaders are shown the file's access scope before the upload completes, so no one shares a link without knowing who can open it.
- **SC-017**: 95% of arrivals and departures appear in other viewers' presence displays within 5 seconds.
- **SC-018**: 99% of viewers who disconnect without leaving cleanly are removed from presence within 60 seconds, so the display never shows people who are not there.
- **SC-019**: Enabling presence causes no measurable regression against SC-002 and SC-003 at the SC-005 concurrency level.
- **SC-020**: Zero cases of presence information revealing a viewer's identity or the viewer count to anyone not authorized to view that file.
- **SC-021**: Presence remains accurate to within one viewer for files with up to 50 simultaneous viewers.
- **SC-022**: Every owner download reproduces 100% of that file's comments, anchored and orphaned alike, with author, time, and anchored passage intact.
- **SC-023**: Service availability is 99.5% or better measured monthly, on a best-effort basis with no formal service guarantee and no stated recovery objective.
- **SC-024**: BlinkMark's own interface passes a WCAG 2.1 Level AA audit with zero Level A or Level AA violations across every user story flow.
- **SC-025**: A person using only a keyboard and a screen reader can complete upload, preview, and anchored commenting end to end, without sighted assistance and without a pointing device.
- **SC-026**: Zero cases of a single user holding more live files than the configured cap, through any interface including agent-initiated uploads.
- **SC-027**: 100% of uploads refused for quota or rate reasons tell the user which limit was hit and how capacity is restored.

## Assumptions

- Every user is a member of a single owning organization directory; guest and cross-organization collaboration is out of scope for this feature. "Organization" and "tenant" are used interchangeably throughout and both mean the owning Microsoft Entra directory.
- A file's link is the access grant: any authenticated member of the organization who holds it can view and comment. Per-file access lists, named invitations, "request access" flows, and security-group scoping are out of scope for this feature. Confidentiality within the organization is therefore governed by who the uploader gives the link to.
- AI agents act only within an active delegated session for a signed-in user. Background, scheduled, and unattended agent work is out of scope for this feature, as is any standing service-level agent identity — the latter would require a constitution amendment.
- The maximum accepted upload size is 10 MB per file, and supported extensions are `.html`, `.htm`, `.md`, and `.markdown`. These are configurable operational limits rather than product promises.
- A single file per upload; multi-file bundles, archives, and HTML with local asset dependencies are out of scope for this feature.
- Files are immutable once uploaded. Producing a corrected draft means uploading a new file, which is a new file with its own comments; re-anchoring comments across versions is out of scope. Orphaning is therefore not caused by users editing content — it arises when an anchor cannot be resolved against the render being displayed, principally because the rendering or sanitization behaviour changed between upload and display, or because a passage is too ambiguous to match confidently.
- Notification delivery uses the organization's existing email and collaboration platform, so no separate messaging subscription or per-message cost is introduced, and no separate recipient address book is maintained.
- Audit entries are written for compliance but are not readable through the product in this phase. There is no administrator or compliance role, no audit browsing or export interface, and no operational runbook describing retrieval — operational documentation is out of scope for this phase. The audit trail's job here is to exist, be complete, and be tamper-resistant.
- Download is deliberately owner-only. A downloaded copy leaves the retention perimeter permanently, so restricting it to the one person who already had the content limits fan-out without making the retention promise meaningless. The file format of the downloaded content-plus-comments bundle is an implementation decision for the plan, not a product promise.
- The service runs best-effort from a single region with local redundancy. There is no disaster recovery, no cross-region failover, no backup, and no restore capability. An infrastructure failure may permanently lose files and their comments; owners hold the original content and re-upload, and lost review commentary is an accepted cost of keeping the service cheap. This trade-off is a deliberate consequence of content being short-lived by design.
- WCAG 2.1 AA conformance covers BlinkMark's own interface — upload, file list, preview chrome, commenting, presence, retention controls. The accessibility of the *uploaded content itself* is the uploader's responsibility and outside BlinkMark's control; the product must not degrade it, but cannot be held to a conformance level for arbitrary third-party HTML it renders.
- Default usage limits are 50 simultaneously live files per user and 20 uploads per hour per user. These are configurable operational values, not product promises. There is no override or exemption mechanism and no administrator to grant one — consistent with there being no administrative role in this phase — so limits must be set high enough that ordinary use never encounters them.
- Retention is measured from original upload time, not from the most recent extension, so extensions cannot be chained to exceed the ceiling.
- Audit entries are retained for at least one year — well beyond the 30-day file ceiling — to serve compliance review after content is gone.
- Delegated agent permission is obtained through the organization's standard consent process; BlinkMark does not define its own consent mechanism.
- Presence is visible to every authorized viewer of a file; there is no invisible or read-only-observer mode, since the feature's value depends on the display being trustworthy. Presence is deduplicated by person, not by connection.
- A viewer is considered gone after roughly a minute without confirmation of activity, trading a brief window of staleness for tolerance of flaky networks.
- Presence is live-only. Historical questions — who opened this file yesterday — are answered by the audit trail, not by presence, which is never stored.
- The interface targets tablet and desktop viewport widths; a dedicated mobile experience is out of scope for this feature.
- Users have accounts in the organization directory already; no account provisioning is part of this feature.
