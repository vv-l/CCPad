/* CCPad integration for the bundled xterm.js 5.3 input textarea.
 * The terminal owns the input line. Its hidden textarea must not retain text
 * that was already delivered, or replace-style IMEs/remote keyboards can use
 * that residue as the next composition's start offset and truncate the commit.
 * Character packets also carry complete input.data after a legacy keypress
 * that may contain only the first character. Preserve that committed text.
 * Use the public addon API plus the host's raw input callback; keep xterm's
 * physical keyboard encoder and IME display.
 */
(function (root) {
  'use strict';

  class CCPadInputCompat {
    constructor(forwardText) {
      if (typeof forwardText !== 'function') throw new Error('A raw terminal input callback is required');
      this.forwardText = forwardText;
    }

    activate(term) {
      const textarea = term.textarea;
      if (!textarea) throw new Error('CCPadInputCompat requires an opened terminal');
      let composing = false;
      let timer = null;
      let generation = 0;
      let disposed = false;
      let packet = null;
      let packetTimer = null;
      let imeKeyDown = false;
      const element = term.element;
      const forwardText = text => {
        if (!disposed && !term.options.disableStdin && text) this.forwardText(text);
      };

      function cancelPacket() {
        if (packetTimer !== null) clearTimeout(packetTimer);
        packetTimer = null;
        packet = null;
      }

      // A remote character packet may contain a whole string. xterm 5.3 reads
      // only keypress.charCode, then suppresses the matching input event, losing
      // its suffix. Observe on the ancestor before xterm's textarea listeners.
      const keypress = event => {
        if (event.target !== textarea) return;
        cancelPacket();
        if (composing || event.isComposing || event.ctrlKey || event.altKey || event.metaKey ||
            event.charCode < 32 || !event.charCode) return;
        // A 229 keydown already scheduled xterm's textarea diff. Suppress a
        // redundant legacy keypress and let that diff send the committed text.
        if (imeKeyDown) { event.stopPropagation(); return; }
        const key = event.key || '';
        // An injected text event has no physical code; its charCode can be
        // stale or incorrect. Wait for input.data instead of sending it first.
        const textPacket = !event.code || event.code === 'Unidentified';
        const deferred = textPacket || (key.length > 1 && !['Unidentified', 'Process', 'Dead', 'AltGraph'].includes(key));
        const current = packet = { emitted: '', deferred };
        if (deferred) event.stopPropagation(); // retain the native text insertion
        packetTimer = setTimeout(() => {
          if (packet !== current || disposed) return;
          cancelPacket();
        }, 0);
      };

      const completePacket = event => {
        if (event.target !== textarea) return;
        const current = packet;
        if (!current || composing || event.isComposing || event.inputType !== 'insertText' ||
            typeof event.data !== 'string') return;
        cancelPacket();
        let remaining = null;
        if (current.deferred) remaining = event.data;
        else if (current.emitted && event.data.startsWith(current.emitted) &&
                 event.data.length > current.emitted.length) remaining = event.data.slice(current.emitted.length);
        if (remaining !== null) {
          // Forward through the same host path as onData, without paste wrappers.
          // The prefix, if any, already reached that path through keypress.
          if (!term.options.screenReaderMode) event.stopPropagation();
          forwardText(remaining);
          scheduleClear(false);
        }
      };

      function cancelClear() {
        ++generation;
        if (timer !== null) clearTimeout(timer);
        timer = null;
      }

      function scheduleClear(afterComposition) {
        cancelClear();
        const expected = generation;
        function clearDeliveredText() {
          if (disposed || expected !== generation || composing) return;
          timer = null;
          // Screen readers rely on xterm's retained value for announcements.
          if (!term.options.screenReaderMode) textarea.value = '';
        }
        timer = setTimeout(() => {
          if (disposed || expected !== generation || composing) return;
          // compositionend fires before xterm's deferred finalize callback.
          // An additional task keeps the value available until it is sent.
          if (afterComposition) timer = setTimeout(clearDeliveredText, 0);
          else clearDeliveredText();
        }, 0);
      }

      const keydown = event => {
        if (event.target !== textarea) return;
        cancelClear(); cancelPacket();
        imeKeyDown = event.keyCode === 229;
        // VK_PACKET is Unicode text injection, not a physical key. Let its
        // subsequent char/input event deliver the full string; canceling the
        // keydown in xterm can prevent those native events altogether.
        if (!composing && !event.isComposing && event.keyCode === 231 &&
            !event.ctrlKey && !event.altKey && !event.metaKey) event.stopPropagation();
      };
      const keyup = () => { imeKeyDown = false; };
      const start = () => { composing = true; imeKeyDown = false; cancelClear(); cancelPacket(); };
      const end = () => { composing = false; scheduleClear(true); };
      const input = event => {
        if (!composing && !event.isComposing) scheduleClear(false);
      };
      element.addEventListener('keydown', keydown, true);
      textarea.addEventListener('keyup', keyup, true);
      textarea.addEventListener('compositionstart', start, true);
      textarea.addEventListener('compositionend', end, true);
      textarea.addEventListener('input', input, true);
      element.addEventListener('keypress', keypress, true);
      element.addEventListener('input', completePacket, true);
      const subscription = term.onData(data => {
        if (packet && !packet.deferred) packet.emitted += data;
        if (!composing) scheduleClear(false);
      });
      this.dispose = () => {
        disposed = true;
        cancelClear();
        cancelPacket();
        subscription.dispose();
        element.removeEventListener('keydown', keydown, true);
        textarea.removeEventListener('keyup', keyup, true);
        textarea.removeEventListener('compositionstart', start, true);
        textarea.removeEventListener('compositionend', end, true);
        textarea.removeEventListener('input', input, true);
        element.removeEventListener('keypress', keypress, true);
        element.removeEventListener('input', completePacket, true);
      };
    }

    dispose() {}
  }

  root.CCPadInputCompat = CCPadInputCompat;
})(globalThis);
