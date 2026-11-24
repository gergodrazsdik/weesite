---
kind: page
title: Privacy policy
lang: en
summary: What this site records about visitors (very little), why, for how long, and your rights.
---

> Sample text for a Weesite site. Replace Example Author, the contact address and the bracketed parts with your own before you publish it.

This site is run by Example Author ("I", "me"), who is responsible for the personal data it processes under the EU General Data Protection Regulation (GDPR). Contact: <author@example.com>.

**In short:** you can read this site without being tracked. There are no advertising or tracking cookies, no third-party scripts, fonts or embeds, and no visitor accounts. Page views are counted without storing IP addresses, and the counts can't be tied to you. If you write through the contact form, your name, address and message are kept until they've been dealt with. If you comment on a post, your name and comment are shown once I've approved them.

## What is recorded when you visit

### Page views

To see which pages people read, the site records each page view with:

- the time, and the page's path (without the query string, so searches aren't recorded);
- the host name of the site that linked to the page, if any (for example `news.example.org`, never the full address);
- whether the request came from a browser, a search engine or other bot, or an AI agent, and the bot's or agent's name;
- a visitor code: a one-way hash of your IP address and browser identification with a random value that changes every day;
- for visits from a browser, the country your IP address belongs to, looked up in the server's memory when the view is counted, in a database of address ranges ([IP to country data by DB-IP](https://db-ip.com)). Only the country is kept, not the address.

Your IP address itself is not stored, and the site sets no cookie to count you. The daily random values are deleted after two days, so the visitor code can't be traced back to you, or connected with your visits on other days. Page views are deleted after 13 months.

Legal basis: legitimate interests (Article 6(1)(f) GDPR) in knowing which content is read and keeping the site useful, with the least possible data.

### Reactions

Posts can take one reaction from you (Like, Love, Thanks, Insightful or Funny), shown only when your browser runs JavaScript. When you react:

- the post's count for that reaction goes up by one, and the reaction is recorded with the time and the post for the site's statistics, without anything about you;
- your browser remembers your reaction in its local storage (`weesite:reactions`), so the page shows it next time. It stays in your browser and isn't sent anywhere; clearing the site's data in your browser removes it;
- you can change or take back your reaction on the same day, and the count changes with it. After that day, taking it back only clears it from your browser: the count stays as it was;
- to stop floods of reactions, the site counts at most one reaction per post per day from the same address and browser, and a limited number an hour from the same address and browser and from the same address. For this it keeps one-way hashes of your IP address, alone and with your browser identification, made with a random value that changes every day, and deletes them after two days. They can't be traced back to you or connected with other days.

Before a reaction is sent, your browser solves a small calculation the site sets (a "proof of work", which usually takes it a second or two); this keeps automated floods expensive without a third-party CAPTCHA, cookies or tracking.

Legal basis: legitimate interests (Article 6(1)(f) GDPR) in letting readers say what they found useful, and in protecting the counts from abuse. Reaction records are deleted after 13 months; the counts stay with the post.

### Technical logs and telemetry

The site's hosting records errors and performance data so problems can be found and fixed: request paths, response times, errors, and an approximate location (country and city) derived from the IP address, which is then discarded. Requests are recorded for a sample of visits; the application's own log messages are kept in full, and none of them contain your IP address. These records are deleted after 30 days.

Legal basis: legitimate interests (Article 6(1)(f) GDPR) in running a secure, working site.

To protect the site, requests to search, the agent interface, sign-in, the contact form, comments and reactions are limited per IP address. The addresses are held in the server's memory for about a minute for this and never written down.

### Cookies

- `theme`: set only when you choose light or dark mode with the switch at the top of the page, to remember your choice for a year. It contains only `light` or `dark`. Because you ask for it, it needs no consent (it's strictly necessary for the function you requested).
- `.AspNetCore.Antiforgery.…` (the end of the name varies): set when you open the contact form, to protect it against forged submissions from other sites. It holds a random security value, nothing about you, and is deleted when you close your browser. It's strictly necessary for sending the form.
- `weesite.editor`: set only in a browser one of the site's authors signs in with, for a month after their last visit to the editor. It keeps their own page views out of the counts and shows them a link to the editor. It contains only `1`.
- No other cookies are set on the public site. The editor (`/admin`), used only by the site's authors, sets a sign-in cookie and security cookies against forged requests.

### Search and the agent interface

Searches on this site, and requests through its interface for AI agents, are processed to answer them and not stored.

### The contact form

If you send a message with the [contact form](/contact), the site keeps:

- your name, your email address and your message, as you typed them;
- when it arrived, and whether your browser passed the site's automatic check against spam bots (a small calculation it does while you type; nothing is sent anywhere else).

Nothing else about you is kept with it, and your IP address isn't stored. To stop floods of messages, the site counts messages per network for an hour, under a one-way code made from your IP address with a random value that changes every day; those counts are deleted after two days.

I use your message to read and answer it. Once an hour at most, new messages, with your name, address and text, are sent to me by email through [your mail provider], as a processor. I delete messages from the site once I've dealt with them, and any that are left are deleted automatically after 12 months. The copy in my mailbox is kept like other email (see Email below).

Legal basis: your request (Article 6(1)(b) GDPR) and my legitimate interest in answering it and keeping the form free of spam (Article 6(1)(f)).

### Comments

If you comment on a post, the site keeps:

- the name you give and your comment, as you typed them, with when you wrote and last edited it;
- a one-way code (a hash) of a key that only your browser gets. Your browser keeps the key in its local storage (`weesite:comments`), with the comment's number, so that it, and only it, can show you your comment while it waits, edit it or delete it. The key isn't sent anywhere else; clearing the site's data in your browser removes it, and then you can't edit or delete the comment yourself any more. Your browser also remembers the name you wrote under (`weesite:commenter`), to fill it in next time; it stays in your browser.

Nothing else about you is kept with a comment, and your IP address isn't stored. To stop floods of comments and edits, the site counts them per network for an hour, under a one-way code made from your IP address with a random value that changes every day; those counts are deleted after two days. As with reactions, your browser solves a small calculation before a comment is sent.

Every comment waits until I've read and approved it; only then are your name and comment shown on the post, to everyone. If you edit an approved comment, the edit waits too, and the post shows the approved text until I've approved the edit; the earlier text isn't kept. Once an hour at most, the comments and edits that wait, with their names and text, are sent by email through [your mail provider], as a processor, to me and to the post's author if someone else wrote it, so we can read them.

- Comments and edits I don't approve, and those still waiting after 30 days, are deleted after 30 days. For an edit, the approved text stays.
- You can delete your comment yourself at any time from the browser you wrote it in; it goes at once. If it had been shown on the post, a "deleted" note without your name or text takes its place, so the replies around it still read. If you can't delete it any more, write to me and I'll remove it.
- If I take an approved comment off the post, a "removed by a moderator" note takes its place, you can no longer edit or delete it, and its name and text are deleted 30 days later.

Comments by the site's authors appear under their name and are marked as theirs.

Legal basis: legitimate interests (Article 6(1)(f) GDPR) in letting readers discuss posts, and in keeping the comments free of spam. Approved comments stay with their post until you or I delete them.

### Email

If you email me, I keep the correspondence as long as needed to reply and follow up, on the basis of your request (Article 6(1)(b) GDPR) or my legitimate interest in answering (Article 6(1)(f)).

## Who processes the data

The site runs on Microsoft Azure in [the European Union region you chose]. Microsoft processes the data above on my behalf under its data protection terms, as a processor. No data is sold or shared with anyone else, and nothing is used for advertising or profiling.

Authors of this site sign in with their Microsoft or Google account; that concerns only them, not readers. When an author is added, their email address goes through [your mail provider], as a processor, to send them an invitation. Messages from the contact form, and notices about comments waiting for approval, reach me through the same provider.

## Your rights

Under the GDPR you have the right to access the personal data about you, to have it corrected or deleted, to restrict or object to its processing, and to data portability. Because page views, reactions and logs can't be linked to you, I usually can't find records about you in them; for anything else, such as emails, contact messages or comments, write to <author@example.com>.

You also have the right to lodge a complaint with a data protection supervisory authority, in particular in the EU country where you live or work, or in [the country where the site's owner is based].

## Changes

If the site starts processing data differently, this page will say so before it does. Last updated: [date].
